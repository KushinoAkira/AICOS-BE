using System.Text.Json;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Billing;
using AICOS.Ordering.Api.Domain.Enums;
using AICOS.Ordering.Api.Features.Orders;
using AICOS.Ordering.Api.Persistence;
using AICOS.Ordering.Api.Services;
using Microsoft.EntityFrameworkCore;
namespace AICOS.Ordering.Api.Features.Qr;

public sealed class DurableTableTransferService(OrderingDbContext db,ICurrentUser user,ITableQrClient tables,TableTransferDispatcher dispatcher)
{
    public async Task<TransferTableResponse> TransferAsync(Guid sessionId,TransferTableRequest request,string key,CancellationToken ct)
    {
        var tenant=TenantScope.Require(user);var actor=user.UserId??throw new UnauthorizedException("Sign in first.");
        if(request.ExpectedTableId is null||string.IsNullOrWhiteSpace(request.TargetQrToken)||request.TargetQrToken.Length>200||key.Length is <8 or >80)throw new ValidationFailedException("transfer","Source, target and Idempotency-Key (8-80 chars) required.");
        var id=new Guid(Convert.FromHexString(BillIdentity.Hash("transfer:"+tenant+":"+actor+":"+key))[..16]);
        var hash=BillIdentity.Hash(JsonSerializer.Serialize(new{sessionId,request}));
        var known=await db.TableTransfers.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id&&x.TenantId==tenant,ct);
        if(known!=null){BranchAccess.EnsureCanAccess(user,known.BranchId);if(known.ActorId!=actor||known.RequestHash!=hash)throw new ConflictException("Transfer key differs.","table.transfer_key_conflict");await dispatcher.TryAsync(id,ct);var latest=await db.TableTransfers.AsNoTracking().SingleAsync(x=>x.Id==id,ct);return new(latest.SessionId,latest.TargetTableId,latest.TargetCode,JsonSerializer.Deserialize<TransferSnapshot>(latest.OrderIdsJson)!.OrderIds.Length,id,latest.SyncedAtUtc==null?"PENDING_SYNC":"COMPLETED");}
        var target=await tables.LookupAsync(request.TargetQrToken,ct)??throw new NotFoundException("Target table",request.ExpectedTableId);
        if(target.TenantId!=tenant)throw new ForbiddenException("Another brand.");BranchAccess.EnsureCanAccess(user,target.BranchId);
        var result=await db.Database.CreateExecutionStrategy().ExecuteAsync(async()=>{
            db.ChangeTracker.Clear();await using var tx=db.Database.IsRelational()?await db.Database.BeginTransactionAsync(ct):null;
            if(tx!=null)await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"table-transfer-session:"+tenant+":"+sessionId},0))",ct);
            var replay=await db.TableTransfers.SingleOrDefaultAsync(x=>x.Id==id&&x.TenantId==tenant,ct);
            if(replay!=null){if(replay.ActorId!=actor||replay.RequestHash!=hash)throw new ConflictException("Transfer key differs.","table.transfer_key_conflict");if(tx!=null)await tx.CommitAsync(ct);return replay;}
            foreach(var tableId in new[]{request.ExpectedTableId.Value,target.TableId}.Distinct().Order())if(tx!=null)await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"aicos:counter-table:"+tenant+":"+tableId},0))",ct);
            var session=await db.TableSessions.SingleOrDefaultAsync(s=>s.Id==sessionId&&s.TenantId==tenant,ct)??throw new NotFoundException("Session",sessionId);BranchAccess.EnsureCanAccess(user,session.BranchId);
            if(!session.IsOpen||session.TableId!=request.ExpectedTableId||session.TableId==target.TableId)throw new ConflictException("Source session changed.","table.transfer_stale");
            if(session.BranchId!=target.BranchId)throw new ForbiddenException("Cannot transfer across branches.");
            if(!target.CanOrder||await db.TableSessions.AnyAsync(s=>s.TenantId==tenant&&s.TableId==target.TableId&&s.Status==AICOS.Ordering.Api.Domain.TableSessionStatus.Open,ct))throw new ConflictException("Target is unavailable.","table.target_occupied");
            if(await db.SessionCheckouts.AnyAsync(x=>x.SessionId==sessionId,ct))throw new ConflictException("Checkout frozen or paid; transfer is blocked.","billing.session_frozen");
            if(await db.TableTransfers.AnyAsync(x=>x.SessionId==sessionId&&x.SyncedAtUtc==null,ct))throw new ConflictException("Previous transfer awaits synchronization.","table.transfer_pending");
            var orders=await db.Orders.Where(o=>o.TableSessionId==sessionId&&o.Status!=OrderStatus.Cancelled&&o.Status!=OrderStatus.NoShow&&o.Status!=OrderStatus.Unfulfillable).ToListAsync(ct);
            if(orders.Any(o=>o.PaymentStatus!=PaymentStatus.Unpaid||o.Status==OrderStatus.Completed))throw new ConflictException("Paid/closed rounds cannot move.","table.transfer_paid");
            foreach(var order in orders)order.RequireWholeOrderOperation();
            var version=(await db.TableTransfers.Where(x=>x.SessionId==sessionId).Select(x=>(long?)x.Version).MaxAsync(ct)??0)+1;
            foreach(var order in orders)order.SwitchServiceType(OrderType.DineIn,target.TableId,target.TableCode,null);
            var sourceCode=session.TableCode;
            session.TransferTo(target.TableId,target.TableCode,DateTime.UtcNow);
            var op=new TableTransferOperation{Id=id,TenantId=tenant,BranchId=session.BranchId,SessionId=sessionId,ActorId=actor,SourceTableId=request.ExpectedTableId.Value,TargetTableId=target.TableId,TargetCode=target.TableCode,Version=version,RequestHash=hash,OrderIdsJson=JsonSerializer.Serialize(new TransferSnapshot(sourceCode,orders.Select(x=>x.Id).ToArray())),CreatedAtUtc=DateTime.UtcNow};db.TableTransfers.Add(op);await db.SaveChangesAsync(ct);if(tx!=null)await tx.CommitAsync(ct);return op;
        });
        await dispatcher.TryAsync(result.Id,ct);var saved=await db.TableTransfers.AsNoTracking().SingleAsync(x=>x.Id==id,ct);
        return new(saved.SessionId,saved.TargetTableId,saved.TargetCode,JsonSerializer.Deserialize<TransferSnapshot>(saved.OrderIdsJson)!.OrderIds.Length,saved.Id,saved.SyncedAtUtc==null?"PENDING_SYNC":"COMPLETED");
    }
}
