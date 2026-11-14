using System.Text.Json;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.Ordering.Api.Features.Orders;
using AICOS.Ordering.Api.Persistence;
using Microsoft.EntityFrameworkCore;
namespace AICOS.Ordering.Api.Features.Qr;
public static class TableTransferHistoryEndpoints
{
    static object View(TableTransferOperation x)=>new{SourceCode=JsonSerializer.Deserialize<TransferSnapshot>(x.OrderIdsJson)!.SourceCode,x.Id,x.SessionId,x.ActorId,x.SourceTableId,TableId=x.TargetTableId,TableCode=x.TargetCode,x.BranchId,x.CreatedAtUtc,x.Version,x.SyncedAtUtc,SyncStatus=x.SyncedAtUtc==null?"PENDING_SYNC":"COMPLETED",OrderIds=JsonSerializer.Deserialize<TransferSnapshot>(x.OrderIdsJson)!.OrderIds};
    public static void MapTransferHistory(this RouteGroupBuilder api)
    {
        var g=api.MapGroup("/qr").RequireAuthorization(p=>p.RequireRole(OrderAccess.CounterRoles)).AddEndpointFilter(async(c,next)=>c.HttpContext.RequestServices.GetRequiredService<IConfiguration>().GetValue<bool>("TableTransfer:Enabled")?await next(c):Results.StatusCode(503));
        g.MapGet("/transfers",async(Guid? branchId,Guid? tableId,OrderingDbContext db,ICurrentUser user,IBranchDirectory directory,CancellationToken ct)=>{
            var branch=await BranchScope.ResolveAsync(user,directory,branchId,false,ct);var tenant=TenantScope.Require(user);var q=db.TableTransfers.AsNoTracking().Where(x=>x.TenantId==tenant&&x.BranchId==branch);if(tableId!=null)q=q.Where(x=>x.SourceTableId==tableId||x.TargetTableId==tableId);var rows=await q.OrderByDescending(x=>x.CreatedAtUtc).Take(100).ToListAsync(ct);return Results.Ok(rows.Select(View));});
        g.MapGet("/transfers/{id:guid}",async(Guid id,OrderingDbContext db,ICurrentUser user,CancellationToken ct)=>{
            var row=await db.TableTransfers.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id&&x.TenantId==TenantScope.Require(user),ct)??throw new NotFoundException("Transfer",id);BranchAccess.EnsureCanAccess(user,row.BranchId);return Results.Ok(View(row));});
        g.MapGet("/sessions/{id:guid}/transfers",async(Guid id,OrderingDbContext db,ICurrentUser user,CancellationToken ct)=>{
            var session=await db.TableSessions.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id&&x.TenantId==TenantScope.Require(user),ct)??throw new NotFoundException("Session",id);BranchAccess.EnsureCanAccess(user,session.BranchId);var rows=await db.TableTransfers.AsNoTracking().Where(x=>x.SessionId==id).OrderByDescending(x=>x.Version).Take(100).ToListAsync(ct);return Results.Ok(rows.Select(View));});
    }
}
