using System.Text.Json;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Billing;
using AICOS.Ordering.Api.Domain.Enums;
using AICOS.Ordering.Api.Features.Orders;
using AICOS.Ordering.Api.Features.Qr;
using AICOS.Ordering.Api.Persistence;
using Microsoft.EntityFrameworkCore;
namespace AICOS.Ordering.Api.Features.SessionBilling;

public static class SessionBillingEndpoints
{
    public static void MapSessionBilling(this WebApplication app, RouteGroupBuilder api)
    {
        api.MapGet("/qr/sessions/{id:guid}/billing-quote", QuoteAsync).RequireAuthorization(AicosPolicies.Staff);
        var internalApi = app.MapGroup("/internal/session-billing").AllowAnonymous()
            .AddEndpointFilter(async (context, next) => BillingMachineAuth.Valid(context.HttpContext,
                context.HttpContext.RequestServices.GetRequiredService<IConfiguration>()) ? await next(context) : Results.Unauthorized());
        internalApi.MapPost("/sessions/{id:guid}/freeze", FreezeAsync);
        internalApi.MapPost("/paid", PaidAsync);
        internalApi.MapGet("/occupancy", async (Guid tenantId, OrderingDbContext db, CancellationToken ct) =>
            await db.TableSessions.AsNoTracking().Where(x => x.TenantId == tenantId && x.Status == AICOS.Ordering.Api.Domain.TableSessionStatus.Open)
                .Select(x => x.TableId).ToListAsync(ct));
    }
    private static async Task<SessionBillQuote> QuoteAsync(Guid id, OrderingDbContext db, ICurrentUser user, IConfiguration config, CancellationToken ct)
    {
        if (!BillingMachineAuth.Enabled(config)) throw new DependencyUnavailableException("session-billing");
        var tenant = TenantScope.Require(user);
        var session = await db.TableSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id && s.TenantId == tenant, ct)
            ?? throw new NotFoundException("Session", id);
        BranchAccess.EnsureCanAccess(user, session.BranchId);
        return await SessionBillQuotes.ReadAsync(db, session, ct);
    }
    private static async Task<SessionBillQuote> FreezeAsync(Guid id, FreezeBillRequest request, OrderingDbContext db, TimeProvider time, CancellationToken ct)
    {
        if (request.BillId != BillIdentity.For(request.TenantId, id)) throw new ConflictException("Bill identity differs.", "billing.scope");
        var session = await db.TableSessions.FirstOrDefaultAsync(s => s.Id == id && s.TenantId == request.TenantId && s.BranchId == request.BranchId, ct)
            ?? throw new NotFoundException("Session", id);
        var table = session.TableId;
        return await TableSessionLock.RunAsync(db, session.TenantId, table, async () =>
        {
            if (db.Database.IsRelational()) await db.Entry(session).ReloadAsync(ct);
            if (!session.IsOpen || session.TableId != table) throw new ConflictException("Session changed.", "qr.session_changed");
            var quote = await SessionBillQuotes.ReadAsync(db, session, ct);
            if (quote.Fingerprint != request.Fingerprint) throw new ConflictException("Bill contents changed. Reload before collecting.", "billing.stale");
            var fence = await db.SessionCheckouts.FirstOrDefaultAsync(x => x.SessionId == id, ct);
            if (fence is null)
            {
                db.SessionCheckouts.Add(new SessionCheckout { SessionId = id, TenantId = request.TenantId, BranchId = request.BranchId,
                    BillId = request.BillId, Fingerprint = quote.Fingerprint, QuoteJson = JsonSerializer.Serialize(quote), CreatedAtUtc = time.GetUtcNow().UtcDateTime });
                await db.SaveChangesAsync(ct);
            }
            else if (fence.BillId != request.BillId) throw new ConflictException("Session already has another bill.", "billing.scope");
            return quote;
        }, ct);
    }
    private static async Task<IResult> PaidAsync(BillPaidNotice notice, OrderingDbContext db, CancellationToken ct)
    {
        var session = await db.TableSessions.FirstOrDefaultAsync(s => s.Id == notice.SessionId && s.TenantId == notice.TenantId && s.BranchId == notice.BranchId, ct)
            ?? throw new NotFoundException("Session", notice.SessionId);
        await TableSessionLock.RunAsync(db, notice.TenantId, session.TableId, async () =>
        {
            var fence = await db.SessionCheckouts.FirstOrDefaultAsync(x => x.SessionId == notice.SessionId, ct)
                ?? throw new ConflictException("Checkout was not prepared.", "billing.not_prepared");
            if (fence.BillId != notice.BillId || fence.Fingerprint != notice.Fingerprint || notice.PaymentId == Guid.Empty)
                throw new ConflictException("Payment proof does not match checkout.", "billing.scope");
            if (fence.PaymentId is not null)
            {
                if (fence.PaymentId != notice.PaymentId) throw new ConflictException("Another payment was already applied.", "billing.reconciliation");
                return true;
            }
            var quote = JsonSerializer.Deserialize<SessionBillQuote>(fence.QuoteJson)!;
            var ids = quote.Orders.Select(o => o.OrderId).ToArray();
            var orders = await db.Orders.Include(o => o.Items).ThenInclude(i => i.Options).Where(o => ids.Contains(o.Id) && o.TableSessionId == session.Id).ToListAsync(ct);
            if (orders.Count != ids.Length || orders.Any(o => o.IsVoided || quote.Orders.Single(x => x.OrderId == o.Id).Amount != o.FinalAmount))
                throw new ConflictException("Frozen orders differ; reconcile received funds.", "billing.reconciliation");
            foreach (var order in orders) order.MarkAsPaid(notice.Method == "Cash" ? PaymentMethod.Cash : PaymentMethod.BankTransfer, notice.Method, notice.PaymentId, notice.PaidAtUtc);
            fence.PaymentId = notice.PaymentId; fence.State = "Paid";
            await db.SaveChangesAsync(ct);
            return true;
        }, ct);
        // Bill payment never closes the session. Financial events use the durable Bill journal.
        return Results.Ok(new { notice.BillId, notice.PaymentId, Applied = true });
    }
}
