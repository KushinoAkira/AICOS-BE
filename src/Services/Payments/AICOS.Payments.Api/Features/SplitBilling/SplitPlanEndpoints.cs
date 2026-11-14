using System.Text.Json;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Billing;
using AICOS.Payments.Api.Features.Payments;
using AICOS.Payments.Api.Features.SessionBilling;
using AICOS.Payments.Api.Persistence;
using Microsoft.EntityFrameworkCore;
namespace AICOS.Payments.Api.Features.SplitBilling;
public static class SplitPlanEndpoints
{
    public static void MapSplitPlans(this RouteGroupBuilder api)
    {
        var g=api.MapGroup("/bills/{id:guid}/split-plan").WithTags("Bill allocation drafts").RequireAuthorization(p=>p.RequireRole(PaymentAccess.CounterRoles))
            .AddEndpointFilter(async(c,next)=>c.HttpContext.RequestServices.GetRequiredService<IConfiguration>().GetValue<bool>("SplitBill:Enabled")?await next(c):Results.StatusCode(503));
        g.MapGet("",GetAsync);g.MapPut("",SaveAsync);g.MapGet("/history",HistoryAsync);
    }
    static async Task<BillView> Scope(Guid id,Guid session,BillLedgerService ledger,ICurrentUser user,CancellationToken ct)
    {
        var bill=await ledger.GetAsync(session,user,ct);if(bill.Id!=id)throw new ConflictException("Bill/session differs.","split.scope");return bill;
    }
    static bool Editable(BillView b)=>b.Status is "DRAFT" or "UNPAID" && !b.NeedsReconciliation && b.Payment?.Status!="PENDING";
    static Dictionary<Guid,int> Totals(IEnumerable<SplitGroup> groups)=>groups.SelectMany(g=>g.Items).GroupBy(i=>i.OrderItemId).ToDictionary(g=>g.Key,g=>g.Sum(i=>i.Quantity));
    static object View(BillView bill,SplitPlan? plan)
    {
        var quote=plan==null?bill.Quote:JsonSerializer.Deserialize<SessionBillQuote>(plan.QuoteJson)!;
        var groups=plan==null?Array.Empty<SplitGroup>():JsonSerializer.Deserialize<SplitGroup[]>(plan.GroupsJson)!;
        var lines=quote.Lines.Where(l=>l.OrderItemId!=null).ToDictionary(l=>l.OrderItemId!.Value);
        var totals=Totals(groups);var allocated=groups.SelectMany(g=>g.Items).Sum(i=>i.Quantity*lines[i.OrderItemId].UnitPrice);
        return new {BillId=bill.Id,bill.SessionId,bill.BranchId,Version=plan?.Version??0,RootStatus=bill.Status,bill.Quote.Fingerprint,RootTotal=bill.Quote.Total,bill.Quote.TableId,bill.Quote.TableCode,Stale=plan!=null&&plan.Fingerprint!=bill.Fingerprint,
            Editable=Editable(bill)&&bill.Quote.Lines.All(l=>l.OrderItemId!=null),PaymentAvailable=false,PaymentBlockedReason="Group payment and discount/tax/fee allocation require approval; no money collected by this plan.",
            Lines=quote.Lines,CurrentLines=bill.Quote.Lines,Groups=groups.Select(g=>new{g.Id,g.Name,g.Items,Subtotal=g.Items.Sum(i=>i.Quantity*lines[i.OrderItemId].UnitPrice),AmountDue=(decimal?)null,PaidAmount=0,Status="DRAFT"}),AllocatedSubtotal=allocated,UnallocatedSubtotal=quote.Subtotal-allocated,
            AllocationComplete=lines.Count>0&&lines.All(x=>totals.GetValueOrDefault(x.Key)==x.Value.Quantity)};
    }
    static async Task<IResult> GetAsync(Guid id,Guid sessionId,PaymentsDbContext db,BillLedgerService ledger,ICurrentUser user,CancellationToken ct)
    {
        var bill=await Scope(id,sessionId,ledger,user,ct);var plan=await db.SplitPlans.AsNoTracking().SingleOrDefaultAsync(p=>p.Id==id&&p.TenantId==TenantScope.Require(user),ct);return Results.Ok(View(bill,plan));
    }
    static async Task<IResult> SaveAsync(Guid id,SaveSplitPlan r,HttpContext http,PaymentsDbContext db,BillLedgerService ledger,ICurrentUser user,CancellationToken ct)
    {
        var key=http.Request.Headers["Idempotency-Key"].ToString();if(key.Length is <8 or >80||r.Groups is null||r.Groups.Count>20||r.Groups.Any(g=>g.Id==Guid.Empty||string.IsNullOrWhiteSpace(g.Name)||g.Name.Length>60||g.Items is null||g.Items.Count>500||g.Items.Any(i=>i.Quantity is <1 or >99)||g.Items.Select(i=>i.OrderItemId).Distinct().Count()!=g.Items.Count)||r.Groups.Select(g=>g.Id).Distinct().Count()!=r.Groups.Count)throw new ValidationFailedException("groups","Valid unique groups, items, quantities and request key required.");
        var bill=await Scope(id,r.SessionId,ledger,user,ct);var actor=user.UserId??throw new UnauthorizedException("Sign in first.");var tenant=TenantScope.Require(user);
        var opId=new Guid(Convert.FromHexString(BillIdentity.Hash("split:"+tenant+":"+actor+":"+key))[..16]);var hash=BillIdentity.Hash(JsonSerializer.Serialize(new{id,r}));
        await BillLock.RunAsync(db,id,async()=>{
            db.ChangeTracker.Clear();var replay=await db.SplitOperations.SingleOrDefaultAsync(x=>x.Id==opId,ct);
            if(replay!=null){if(replay.ActorId!=actor||replay.PlanId!=id||replay.RequestHash!=hash)throw new ConflictException("Split request key differs.","split.key_conflict");return true;}
            var savedBill=await db.SessionBills.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct);
            if(!Editable(bill)||savedBill?.Status is "PAID" or "CLOSED"||savedBill?.NeedsReconciliation==true||await db.BillAttempts.AnyAsync(x=>x.BillId==id&&x.Status=="PENDING",ct))throw new ConflictException("Bill frozen, pending or paid.","split.not_editable");
            if(r.Fingerprint!=bill.Fingerprint||bill.Quote.Lines.Any(l=>l.OrderItemId is null)||bill.Quote.Lines.Select(l=>l.OrderItemId).Distinct().Count()!=bill.Quote.Lines.Count)throw new ConflictException("Reload a quote with stable item identities.","split.stale_quote");
            var source=bill.Quote.Lines.ToDictionary(l=>l.OrderItemId!.Value);var totals=Totals(r.Groups);
            if(totals.Any(x=>!source.ContainsKey(x.Key)||x.Value>source[x.Key].Quantity))throw new ConflictException("Allocation exceeds bill item quantities.","split.quantity");
            var plan=await db.SplitPlans.SingleOrDefaultAsync(p=>p.Id==id,ct);if((plan?.Version??0)!=r.Version)throw new ConflictException("Allocation changed.","split.stale");
            var before=plan?.GroupsJson??"[]";var oldGroups=JsonSerializer.Deserialize<SplitGroup[]>(before)!;
            if(oldGroups.Any(g=>r.Groups.All(n=>n.Id!=g.Id))&&Totals(oldGroups).Any(x=>totals.GetValueOrDefault(x.Key)<x.Value))throw new ConflictException("Move allocated quantities before deleting or merging a group.","split.delete_loses_allocation");
            if(plan==null){plan=new SplitPlan{Id=id,TenantId=tenant,BranchId=bill.BranchId,SessionId=r.SessionId};db.SplitPlans.Add(plan);}
            plan.Version++;plan.Fingerprint=bill.Fingerprint;plan.QuoteJson=JsonSerializer.Serialize(bill.Quote);plan.GroupsJson=JsonSerializer.Serialize(r.Groups);plan.UpdatedBy=actor;plan.UpdatedAtUtc=DateTime.UtcNow;
            db.SplitOperations.Add(new(){Id=opId,PlanId=id,ActorId=actor,RequestHash=hash,BeforeJson=before,AfterJson=plan.GroupsJson,CreatedAtUtc=plan.UpdatedAtUtc});await db.SaveChangesAsync(ct);return true;
        },ct,key:"split:"+opId);
        return await GetAsync(id,r.SessionId,db,ledger,user,ct);
    }
    static async Task<IResult> HistoryAsync(Guid id,Guid sessionId,PaymentsDbContext db,BillLedgerService ledger,ICurrentUser user,CancellationToken ct)
    {
        await Scope(id,sessionId,ledger,user,ct);return Results.Ok(await db.SplitOperations.AsNoTracking().Where(x=>x.PlanId==id).OrderByDescending(x=>x.CreatedAtUtc).Take(100).Select(x=>new{x.Id,x.ActorId,x.CreatedAtUtc,x.BeforeJson,x.AfterJson}).ToListAsync(ct));
    }
}
