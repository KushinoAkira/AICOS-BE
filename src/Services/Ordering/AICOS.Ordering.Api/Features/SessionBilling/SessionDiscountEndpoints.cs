using System.Text.Json;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Billing;
using AICOS.Ordering.Api.Domain;
using AICOS.Ordering.Api.Domain.Entities;
using AICOS.Ordering.Api.Features.Orders;
using AICOS.Ordering.Api.Features.Qr;
using AICOS.Ordering.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.Ordering.Api.Features.SessionBilling;

public sealed record SetSessionDiscount(string? ExpectedFingerprint, long? ExpectedRevision, string? Mode, decimal? Value, string? Reason);
public sealed record SessionDiscountView(Guid SessionId, Guid BranchId, string TableCode, string Fingerprint,
    long Revision, decimal Subtotal, decimal Discount, decimal Total, bool CanEdit, string? ReasonCode,
    Guid? OperationId, string? Mode, decimal? Value, bool Replay = false, bool ReviewRequired = false);

public static class SessionDiscountEndpoints
{
    public static void MapSessionDiscounts(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/qr/sessions/{id:guid}/manual-discount")
            .WithTags("Session manual discounts").RequireAuthorization(p => p.RequireRole(OrderAccess.CounterRoles))
            .AddEndpointFilter(async (context, next) =>
            {
                var config = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
                if (!config.GetValue<bool>("SessionDiscount:Enabled") || !config.GetValue<bool>("SessionDiscount:GuardEnabled")
                    || !BillingMachineAuth.Enabled(config))
                    return Results.StatusCode(503);
                var services = context.HttpContext.RequestServices;
                await services.GetRequiredService<IDiscountActorVerifier>()
                    .EnsureAsync(services.GetRequiredService<ICurrentUser>(), context.HttpContext.RequestAborted);
                return await next(context);
            });
        group.MapGet("", ReadAsync);
        group.MapPut("", SetAsync);
    }

    private static async Task<TableSession> Scope(Guid id, OrderingDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var tenant = TenantScope.Require(user);
        var session = await db.TableSessions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenant, ct)
            ?? throw new NotFoundException("Session", id);
        BranchAccess.EnsureCanAccess(user, session.BranchId);
        return session;
    }

    private static Task<SessionDiscountEvent?> Latest(OrderingDbContext db, TableSession session, CancellationToken ct) =>
        db.SessionDiscountEvents.AsNoTracking().Where(x => x.SessionId == session.Id && x.TenantId == session.TenantId)
            .OrderByDescending(x => x.Revision).FirstOrDefaultAsync(ct);

    private static Task<List<Order>> Orders(OrderingDbContext db, Guid session, CancellationToken ct) =>
        db.Orders.Include(x => x.Items).ThenInclude(x => x.Options).Where(x => x.TableSessionId == session).ToListAsync(ct);

    private static string? Conflict(IReadOnlyList<Order> orders, SessionDiscountEvent? latest)
    {
        if (orders.Any(x => x.PaymentStatus != AICOS.Ordering.Api.Domain.Enums.PaymentStatus.Unpaid || x.PayFirst))
            return "discount.order_locked";
        if (orders.Any(x => !string.IsNullOrWhiteSpace(x.PromotionCode))) return "discount.promotion_exists";
        var before = latest is null ? new Dictionary<Guid, decimal>() :
            JsonSerializer.Deserialize<DiscountAllocation[]>(latest.AllocationsJson)!.ToDictionary(x => x.OrderId, x => x.After);
        return orders.Any(x => x.DiscountAmount != before.GetValueOrDefault(x.Id)) ? "discount.reconciliation" : null;
    }

    private static async Task<SessionDiscountView> View(OrderingDbContext db, TableSession session, IConfiguration config, CancellationToken ct)
    {
        var quote = await SessionBillQuotes.ReadAsync(db, session, ct);
        var latest = await Latest(db, session, ct);
        var live = (await Orders(db, session.Id, ct)).Where(x => !x.IsVoided).ToList();
        var reason = !session.IsOpen || await db.SessionCheckouts.AnyAsync(x => x.SessionId == session.Id, ct)
            ? "discount.locked" : Conflict(live, latest);
        if (live.Any(x => x.TenantId != session.TenantId || x.BranchId != session.BranchId)) reason = "discount.reconciliation";
        if (reason is null && config.GetValue<bool>("TableTransfer:Enabled") &&
            await db.TableTransfers.AnyAsync(x => x.TenantId == session.TenantId && x.SessionId == session.Id && x.SyncedAtUtc == null, ct))
            reason = "discount.transfer_pending";
        return new(session.Id, session.BranchId, session.TableCode, quote.Fingerprint, latest?.Revision ?? 0,
            quote.Subtotal, quote.Discount, quote.Total, reason is null, reason, latest?.Id, latest?.Mode, latest?.Value,
            ReviewRequired: latest?.RequiresReview(quote.Fingerprint) == true);
    }

    private static async Task<SessionDiscountView> ReadAsync(Guid id, OrderingDbContext db, ICurrentUser user, IConfiguration config, CancellationToken ct)
    {
        var before = await Scope(id, db, user, ct);
        return await TableSessionLock.RunAsync(db, before.TenantId, before.TableId, async () =>
        {
            var session = await Scope(id, db, user, ct);
            if (session.TableId != before.TableId) throw new ConflictException("Table changed.", "qr.session_changed");
            return await View(db, session, config, ct);
        }, ct);
    }

    private static async Task<SessionDiscountView> SetAsync(Guid id, SetSessionDiscount r, HttpContext http,
        OrderingDbContext db, ICurrentUser user, IConfiguration config, TimeProvider clock, CancellationToken ct)
    {
        var key = http.Request.Headers["Idempotency-Key"].ToString().Trim();
        var reason = r.Reason?.Trim();
        if (key.Length is < 8 or > 80 || r.ExpectedFingerprint is not { Length: 64 }
            || r.ExpectedFingerprint.Any(c => !Uri.IsHexDigit(c)) || r.ExpectedRevision is null or < 0
            || r.Mode is not ("AMOUNT" or "PERCENT") || r.Value is null or < 0 or > 9_999_999_999_999_999m
            || reason is null || reason.Length is < 5 or > 250)
            throw new ValidationFailedException("discount", "Valid quote, revision, mode, value, reason and request key required.");
        var before = await Scope(id, db, user, ct);
        var actor = user.UserId ?? throw new UnauthorizedException("Sign in first.");
        var operation = new Guid(Convert.FromHexString(BillIdentity.Hash($"manual-discount:{before.TenantId}:{actor}:{key}"))[..16]);
        var hash = BillIdentity.Hash(JsonSerializer.Serialize(new { id, r.ExpectedFingerprint, r.ExpectedRevision, r.Mode, r.Value, Reason = reason }));
        return await TableSessionLock.RunAsync(db, before.TenantId, before.TableId, async () =>
        {
            db.ChangeTracker.Clear();
            if (db.Database.IsRelational())
            {
                var operationLock = $"aicos:manual-discount:{operation}";
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({operationLock}, 0))", ct);
            }
            var session = await Scope(id, db, user, ct);
            if (session.TableId != before.TableId) throw new ConflictException("Table changed.", "qr.session_changed");
            var previous = await db.SessionDiscountEvents.AsNoTracking().FirstOrDefaultAsync(x => x.Id == operation, ct);
            if (previous is not null)
            {
                if (previous.RequestHash != hash || previous.SessionId != id || previous.ActorId != actor || previous.TenantId != before.TenantId)
                    throw new ConflictException("Request key reused for different input.", "discount.key_conflict");
                return JsonSerializer.Deserialize<SessionDiscountView>(previous.ResultJson)! with { Replay = true };
            }
            var current = await View(db, session, config, ct);
            if (!current.CanEdit) throw new ConflictException("This discount cannot be changed.", current.ReasonCode!);
            if (current.Fingerprint != r.ExpectedFingerprint || current.Revision != r.ExpectedRevision)
                throw new ConflictException("Quote or discount revision changed.", "discount.stale");
            var orders = (await Orders(db, id, ct)).Where(x => !x.IsVoided).ToList();
            var allocations = SessionDiscountCalculation.Allocate(orders, r.Mode!, r.Value!.Value);
            foreach (var allocation in allocations) orders.Single(x => x.Id == allocation.OrderId).SetSessionManualDiscount(allocation.After);
            await db.SaveChangesAsync(ct);
            var quote = await SessionBillQuotes.ReadAsync(db, session, ct);
            var result = new SessionDiscountView(id, session.BranchId, session.TableCode, quote.Fingerprint,
                checked(current.Revision + 1), quote.Subtotal, quote.Discount, quote.Total, true, null, operation, r.Mode, r.Value);
            db.SessionDiscountEvents.Add(new SessionDiscountEvent
            {
                Id = operation, TenantId = session.TenantId, BranchId = session.BranchId, SessionId = id, ActorId = actor,
                Revision = result.Revision, RequestHash = hash, Reason = reason, Mode = r.Mode!, Value = r.Value.Value,
                AllocationsJson = JsonSerializer.Serialize(allocations), ResultJson = JsonSerializer.Serialize(result),
                CreatedAtUtc = clock.GetUtcNow().UtcDateTime
            });
            await db.SaveChangesAsync(ct);
            return result;
        }, ct);
    }
}
