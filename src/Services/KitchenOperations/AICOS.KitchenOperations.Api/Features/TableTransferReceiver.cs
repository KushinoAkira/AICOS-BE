using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;
using AICOS.KitchenOperations.Api.Persistence;
using Microsoft.EntityFrameworkCore;
namespace AICOS.KitchenOperations.Api.Features;
public static class TableTransferReceiver
{
    public static void MapTableTransferReceiver(this WebApplication app)=>app.MapPost("/internal/table-transfers",ReceiveAsync).AllowAnonymous();
    public static async Task<IResult> ReceiveAsync(DurableTableTransfer r,HttpContext http,IConfiguration config,KitchenOperationsDbContext db,CancellationToken ct)
    {
        if(!TableTransferMachineAuth.Valid(http,config))return Results.Unauthorized();
        if(r.OperationId==Guid.Empty||r.TenantId==Guid.Empty||r.BranchId==Guid.Empty||r.Version<=0||string.IsNullOrWhiteSpace(r.TargetCode)||r.TargetCode.Length>20||r.OrderIds is null||r.OrderIds.Count>100||r.OrderIds.Distinct().Count()!=r.OrderIds.Count)throw new ValidationFailedException("transfer","Invalid routing receipt.");
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async()=>{
            db.ChangeTracker.Clear();await using var tx=db.Database.IsRelational()?await db.Database.BeginTransactionAsync(ct):null;
            foreach(var order in r.OrderIds.Order())if(tx!=null)await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"aicos:kitchen-operation:"+r.TenantId+":"+order},0))",ct);
            var tickets=await db.Tickets.Where(t=>r.OrderIds.Contains(t.OrderId)&&t.TenantId==r.TenantId&&t.BranchId==r.BranchId).ToListAsync(ct);
            if(tickets.Count!=r.OrderIds.Count)return (IResult)Results.Json(new TableTransferAck(r.OperationId,r.Version,false),statusCode:202);
            foreach(var ticket in tickets){ticket.RequireWholeOrderOperation();ticket.ApplyTableTransfer(r.TargetCode,r.Version);}
            await db.SaveChangesAsync(ct);if(tx!=null)await tx.CommitAsync(ct);return Results.Ok(new TableTransferAck(r.OperationId,r.Version,true));
        });
    }
}
