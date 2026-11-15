using AICOS.Contracts.Billing;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.Payments.Api.Features.Payments;
using AICOS.Payments.Api.Features.SessionBilling;
using AICOS.Payments.Api.Persistence;
using Microsoft.EntityFrameworkCore;
namespace AICOS.Payments.Api.Features.CashierShifts;

public sealed record OpenCashierShift(Guid? BranchId, decimal OpeningCash, string? Note);
public sealed record CloseCashierShift(long Version, decimal CountedCash, string? Note);
public static class CashierShiftEndpoints
{
    public static void MapCashierShifts(this RouteGroupBuilder api)
    {
        api.MapGet("/cashier-shifts/cash-precheck", CashPrecheck.GetAsync).WithTags("Cashier money shifts").RequireAuthorization(b => b.RequireRole(PaymentAccess.CounterRoles));
        var g=api.MapGroup("/cashier-shifts").WithTags("Cashier money shifts").RequireAuthorization(b=>b.RequireRole(PaymentAccess.CounterRoles))
            .AddEndpointFilter(async(c,next)=>c.HttpContext.RequestServices.GetRequiredService<IConfiguration>().GetValue<bool>("CashierShifts:Enabled") ? await next(c) : Results.StatusCode(503));
        g.MapPost("/",OpenAsync); g.MapPost("/{id:guid}/close",CloseAsync);
        g.MapGet("/",ListAsync); g.MapGet("/{id:guid}",GetAsync); g.MapGet("/{id:guid}/transactions",TransactionsAsync);
    }
    static Guid Actor(ICurrentUser u)=>u.UserId??throw new UnauthorizedException("Sign in first.");
    static bool Manager(ICurrentUser u)=>u.IsInRole(AicosRoles.BranchManager)||u.IsInRole(AicosRoles.BrandAdmin);
    static void Money(decimal value){if(value<0 || value>1_000_000_000m || value!=decimal.Truncate(value)) throw new ValidationFailedException("amount","Whole VND in range 0..1000000000 required.");}
    static void Note(string? value){if(value?.Length>500) throw new ValidationFailedException("note","Maximum 500 characters.");}
    static async Task<CashierShift> Load(PaymentsDbContext db,Guid id,ICurrentUser u,CancellationToken ct)
    {
        var s=await db.CashierShifts.FirstOrDefaultAsync(s=>s.Id==id && s.TenantId==TenantScope.Require(u),ct)??throw new NotFoundException("Cashier shift",id);
        BranchAccess.EnsureCanAccess(u,s.BranchId); if(s.CashierId!=Actor(u)&&!Manager(u)) throw new ForbiddenException("Another cashier owns this shift."); return s;
    }
    static async Task<bool> NeedsReview(PaymentsDbContext db,List<CollectionOutbox> rows,CancellationToken ct)
    {
        var ids=rows.Select(x=>x.Id).ToArray();
        var bills=rows.Select(x=>System.Text.Json.JsonSerializer.Deserialize<AICOS.Contracts.Billing.CollectionReceipt>(x.Payload)?.BillId).Where(x=>x!=null).Select(x=>x!.Value).ToArray();
        return await db.Payments.AnyAsync(x=>ids.Contains(x.Id)&&x.RefundedAmount>0,ct)
            || await db.PaymentRefunds.AnyAsync(x=>ids.Contains(x.PaymentId)&&(x.Status==AICOS.Payments.Api.Domain.RefundStatus.PendingApproval||x.Status==AICOS.Payments.Api.Domain.RefundStatus.Failed),ct)
            || await db.SessionBills.AnyAsync(x=>bills.Contains(x.Id)&&x.NeedsReconciliation,ct);
    }
    static async Task<object> View(PaymentsDbContext db,CashierShift s,CancellationToken ct)
    {
        var rows=await db.Collections.AsNoTracking().Where(x=>x.ShiftId==s.Id).ToListAsync(ct);
        var cash=rows.Where(x=>x.Method=="Cash").Sum(x=>x.Amount);
        var refundRisk=await NeedsReview(db,rows,ct);
        return new {s.Id,Code="CA-"+s.Id.ToString("N")[..8].ToUpperInvariant(),s.BranchId,s.CashierId,Status=s.ClosedAtUtc==null?"OPEN":"CLOSED",s.OpenedAtUtc,s.ClosedAtUtc,s.OpeningCash,s.Note,s.Version,
            CashCollected=cash,QrCollected=(decimal?)null,QrAllocation="UNALLOCATED_BRANCH_ONLY",Transactions=rows.Count,PaidBills=rows.Count(x=>System.Text.Json.JsonSerializer.Deserialize<AICOS.Contracts.Billing.CollectionReceipt>(x.Payload)?.BillId!=null),
            ReportingPending=rows.Count(x=>x.DeliveredAtUtc==null),TotalCollected=cash,ExpectedCash=s.ExpectedCash??s.OpeningCash+cash,s.CountedCash,Difference=s.CountedCash-s.ExpectedCash,s.ClosingNote,
            CanClose=s.ClosedAtUtc==null&&!refundRisk,NeedsReconciliation=refundRisk,RefundedAmount=(decimal?)null,
            Blocker=refundRisk?"Resolve payment reconciliation or approve refund allocation policy.":null};
    }
    static async Task<IResult> OpenAsync(OpenCashierShift r,HttpContext http,PaymentsDbContext db,ICurrentUser u,IBranchDirectory directory,CancellationToken ct)
    {
        Money(r.OpeningCash); Note(r.Note);var actor=Actor(u);var tenant=TenantScope.Require(u);
        var branch=await BranchScope.ResolveAsync(u,directory,r.BranchId,true,ct);
        var key=http.Request.Headers["Idempotency-Key"].ToString();if(key.Length is <8 or >80)throw new ValidationFailedException("Idempotency-Key","8-80 characters required.");
        var hash=BillIdentity.Hash(tenant+":"+actor+":"+key);var id=new Guid(Convert.FromHexString(hash)[..16]);
        return await BillLock.RunAsync(db,id,async()=>{
            await CashierLedger.ActorLockAsync(db,tenant,actor,ct);db.ChangeTracker.Clear();
            var old=await db.CashierShifts.FirstOrDefaultAsync(s=>s.TenantId==tenant&&s.CashierId==actor&&s.OpenKey==hash,ct);
            if(old!=null){if(old.BranchId!=branch||old.OpeningCash!=r.OpeningCash||old.Note!=r.Note)throw new ConflictException("Open request differs.","cashier_shift.key_conflict");return Results.Ok(await View(db,old,ct));}
            if(await db.CashierShifts.AnyAsync(s=>s.TenantId==tenant&&s.CashierId==actor&&s.ClosedAtUtc==null,ct))throw new ConflictException("A money shift is already open.","cashier_shift.already_open");
            var s=new CashierShift{Id=id,TenantId=tenant,BranchId=branch,CashierId=actor,OpenKey=hash,OpeningCash=r.OpeningCash,Note=r.Note,OpenedAtUtc=DateTime.UtcNow};db.CashierShifts.Add(s);await db.SaveChangesAsync(ct);return Results.Ok(await View(db,s,ct));
        },ct);
    }
    static async Task<IResult> CloseAsync(Guid id,CloseCashierShift r,PaymentsDbContext db,ICurrentUser u,CancellationToken ct)
    {
        Money(r.CountedCash);Note(r.Note);var tenant=TenantScope.Require(u);var actor=Actor(u);
        return await BillLock.RunAsync(db,id,async()=>{
            await CashierLedger.ActorLockAsync(db,tenant,actor,ct);db.ChangeTracker.Clear();var s=await Load(db,id,u,ct);
            if(s.CashierId!=actor)throw new ForbiddenException("Only the responsible cashier counts this drawer.");
            if(s.ClosedAtUtc!=null){if(s.CountedCash==r.CountedCash&&s.ClosingNote==r.Note)return Results.Ok(await View(db,s,ct));throw new ConflictException("Closed reconciliation is immutable.","cashier_shift.closed");}
            if(s.Version!=r.Version)throw new ConflictException("Reload the shift.","cashier_shift.stale");
            var rows=await db.Collections.Where(x=>x.ShiftId==id).ToListAsync(ct);
            if(await NeedsReview(db,rows,ct))throw new ConflictException("Resolve payment/refund reconciliation before closing.","cashier_shift.reconciliation");
            var expected=s.OpeningCash+rows.Where(x=>x.Method=="Cash").Sum(x=>x.Amount);
            if(expected!=r.CountedCash&&string.IsNullOrWhiteSpace(r.Note))throw new ValidationFailedException("note","Explain the counted difference; ledger will not be adjusted.");
            s.ExpectedCash=expected;s.CountedCash=r.CountedCash;s.ClosingNote=r.Note;s.ClosedAtUtc=DateTime.UtcNow;s.Version++;await db.SaveChangesAsync(ct);return Results.Ok(await View(db,s,ct));
        },ct);
    }
    static async Task<IResult> ListAsync(Guid? branchId,DateTime? from,DateTime? to,string? status,PaymentsDbContext db,ICurrentUser u,IBranchDirectory directory,CancellationToken ct)
    {
        var branch=await BranchScope.ResolveAsync(u,directory,branchId,false,ct);var tenant=TenantScope.Require(u);var actor=Actor(u);
        var q=db.CashierShifts.AsNoTracking().Where(s=>s.TenantId==tenant&&s.BranchId==branch);
        if(!Manager(u))q=q.Where(s=>s.CashierId==actor);if(from!=null)q=q.Where(s=>s.OpenedAtUtc>=from);if(to!=null)q=q.Where(s=>s.OpenedAtUtc<to);if(status=="OPEN")q=q.Where(s=>s.ClosedAtUtc==null);if(status=="CLOSED")q=q.Where(s=>s.ClosedAtUtc!=null);
        var rows=await q.OrderByDescending(s=>s.OpenedAtUtc).Take(100).ToListAsync(ct);var views=new List<object>();foreach(var s in rows)views.Add(await View(db,s,ct));return Results.Ok(views);
    }
    static async Task<IResult> GetAsync(Guid id,PaymentsDbContext db,ICurrentUser u,CancellationToken ct)=>Results.Ok(await View(db,await Load(db,id,u,ct),ct));
    static async Task<IResult> TransactionsAsync(Guid id,PaymentsDbContext db,ICurrentUser u,CancellationToken ct){await Load(db,id,u,ct);var rows=await db.Collections.AsNoTracking().Where(x=>x.ShiftId==id).OrderByDescending(x=>x.PaidAtUtc).Take(100).ToListAsync(ct);return Results.Ok(rows.Select(x=>System.Text.Json.JsonSerializer.Deserialize<AICOS.Contracts.Billing.CollectionReceipt>(x.Payload)));}
}
