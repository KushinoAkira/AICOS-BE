using System.Text.Json;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Billing;
using AICOS.Ordering.Api.Domain.Entities;
using AICOS.Ordering.Api.Features.Orders;
using AICOS.Ordering.Api.Features.SessionBilling;
using AICOS.Ordering.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.Ordering.Api.Features.OrderCancellation;

public sealed record RequestOrderCancellation(Guid? ExpectedSessionId, string? ExpectedFingerprint, string? Reason);
public sealed record CancellationOperationView(Guid Id, Guid OrderId, Guid SessionId, Guid BranchId, string State,
    string Reason, string? ReasonCode, long Version, bool Active, DateTime CreatedAtUtc, DateTime? CompletedAtUtc);
public sealed record OrderCancellationView(Guid OrderId, Guid BranchId, Guid? SessionId, string OrderCode, string OrderStatus,
    decimal Amount, string Fingerprint, bool CanRequest, string? ReasonCode, CancellationOperationView? Operation);

public static class OrderCancellationEndpoints
{
    public static void MapOrderCancellations(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/{orderId:guid}/cancellation").WithTags("Safe order cancellation")
            .RequireAuthorization(p => p.RequireRole(OrderAccess.CounterRoles))
            .AddEndpointFilter(async (context, next) =>
            {
                var services = context.HttpContext.RequestServices; var config = services.GetRequiredService<IConfiguration>();
                if (!OrderCancellationMachineAuth.GuardEnabled(config) || !BillingMachineAuth.Enabled(config))
                    return Results.StatusCode(503);
                await services.GetRequiredService<IDiscountActorVerifier>().EnsureAsync(services.GetRequiredService<ICurrentUser>(), context.HttpContext.RequestAborted);
                return await next(context);
            });
        group.MapGet("", GetAsync);
        group.MapPost("", RequestAsync);
        group.MapPost("/{operationId:guid}/retry", RetryAsync);
    }

    private static CancellationOperationView View(OrderCancellationOperation op) => new(op.Id, op.OrderId, op.SessionId,
        op.BranchId, op.State, op.Reason, op.ReasonCode, op.Version, op.CompletedAtUtc is null, op.CreatedAtUtc, op.CompletedAtUtc);

    private static async Task<string?> Blocked(Order order, OrderingDbContext db, CancellationToken ct)
    {
        if (OrderCancellationRules.Blocked(order) is { } reason) return reason;
        var session = await db.TableSessions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == order.TableSessionId && x.TenantId == order.TenantId, ct);
        if (session is null || !session.IsOpen || session.TableId != order.TableId || session.BranchId != order.BranchId) return "qr.session_changed";
        if (await db.SessionCheckouts.AnyAsync(x => x.SessionId == session.Id, ct)) return "billing.session_frozen";
        if (await db.TableTransfers.AnyAsync(x => x.SessionId == session.Id && x.SyncedAtUtc == null, ct)) return "table.transfer_pending";
        return null;
    }

    private static async Task<OrderCancellationView> GetAsync(Guid orderId, OrderingDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var order = await OrderAccess.LoadAsync(db, user, orderId, ct, track: false);
        var operation = await db.Cancellations.AsNoTracking().Where(x => x.OrderId == orderId && x.TenantId == order.TenantId)
            .OrderBy(x => x.CompletedAtUtc != null).ThenByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id).FirstOrDefaultAsync(ct);
        var reason = operation is { CompletedAtUtc: null } ? "order.cancel_pending"
            : operation?.State == "REJECTED" ? operation.ReasonCode : await Blocked(order, db, ct);
        return new(order.Id, order.BranchId, order.TableSessionId, order.OrderCode, order.Status.ToString(), order.FinalAmount,
            OrderCancellationRules.Fingerprint(order), reason is null, reason, operation is null ? null : View(operation));
    }

    private static async Task<IResult> RequestAsync(Guid orderId, RequestOrderCancellation r, HttpContext http, OrderingDbContext db,
        ICurrentUser user, OrderCancellationDispatcher dispatcher, TimeProvider clock, IConfiguration config, CancellationToken ct)
    {
        if (!config.GetValue<bool>("OrderCancellation:Enabled")) return Results.StatusCode(503);
        var key = http.Request.Headers["Idempotency-Key"].ToString().Trim(); var reason = r.Reason?.Trim();
        if (r.ExpectedSessionId is null || r.ExpectedFingerprint is not { Length: 64 } || r.ExpectedFingerprint.Any(x => !Uri.IsHexDigit(x))
            || reason is null || reason.Length is < 5 or > 250 || key.Length is < 8 or > 80)
            throw new ValidationFailedException("cancellation", "Verified session/fingerprint, 5-250 character reason and request key required.");
        var hint = await OrderAccess.LoadAsync(db, user, orderId, ct, track: false);
        if (hint.TableId is null) throw new ConflictException("Use a verified staff table session.", "order.cancel_session_required");
        var actor = user.UserId ?? throw new UnauthorizedException("Sign in first.");
        var id = new Guid(Convert.FromHexString(BillIdentity.Hash($"order-cancel:{hint.TenantId}:{actor}:{key}"))[..16]);
        var hash = BillIdentity.Hash(JsonSerializer.Serialize(new { orderId, r.ExpectedSessionId, r.ExpectedFingerprint, Reason = reason }));
        await OrderCancellationRules.Locked(db, hint.TenantId, orderId, hint.TableId.Value, async () =>
        {
            if (db.Database.IsRelational())
            {
                var opLock = $"aicos:order-cancel-key:{id}";
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({opLock}, 0))", ct);
            }
            var existing = await db.Cancellations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (existing is not null)
            {
                if (existing.TenantId != hint.TenantId || existing.OrderId != orderId || existing.ActorId != actor || existing.RequestHash != hash)
                    throw new ConflictException("Request key differs.", "order.cancel_key_conflict");
                return true;
            }
            var order = await OrderAccess.LoadAsync(db, user, orderId, ct);
            if (await Blocked(order, db, ct) is { } blocked) throw new ConflictException("Order cannot be cancelled directly.", blocked);
            if (order.TableId != hint.TableId || order.TableSessionId != r.ExpectedSessionId || OrderCancellationRules.Fingerprint(order) != r.ExpectedFingerprint)
                throw new ConflictException("Order or session changed.", "order.cancel_stale");
            if (await db.Cancellations.AnyAsync(x => x.OrderId == orderId && x.TenantId == order.TenantId && x.CompletedAtUtc == null, ct))
                throw new ConflictException("Inspect the pending cancellation first.", "order.cancel_pending");
            var now = clock.GetUtcNow().UtcDateTime;
            db.Cancellations.Add(new() { Id = id, TenantId = order.TenantId, BranchId = order.BranchId, SessionId = r.ExpectedSessionId.Value,
                TableId = hint.TableId.Value, OrderId = order.Id, ActorId = actor, RequestHash = hash, Fingerprint = r.ExpectedFingerprint,
                OrderCode = order.OrderCode, TableCode = order.TableNumber!, Reason = reason, BeforeJson = OrderCancellationRules.Snapshot(order),
                CreatedAtUtc = now, NextAttemptAtUtc = now });
            await db.SaveChangesAsync(ct); return true;
        }, ct);
        await dispatcher.TryAsync(id, ct);
        var result = await db.Cancellations.AsNoTracking().SingleAsync(x => x.Id == id, ct);
        return Results.Json(View(result), statusCode: result.CompletedAtUtc is null ? 202 : 200);
    }

    private static async Task<IResult> RetryAsync(Guid orderId, Guid operationId, OrderingDbContext db, ICurrentUser user,
        OrderCancellationDispatcher dispatcher, CancellationToken ct)
    {
        var order = await OrderAccess.LoadAsync(db, user, orderId, ct, track: false);
        var op = await db.Cancellations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == operationId && x.OrderId == orderId && x.TenantId == order.TenantId, ct)
            ?? throw new NotFoundException("Cancellation", operationId);
        BranchAccess.EnsureCanAccess(user, op.BranchId);
        await dispatcher.TryAsync(operationId, ct);
        var current = await db.Cancellations.AsNoTracking().SingleAsync(x => x.Id == operationId, ct);
        return Results.Json(View(current), statusCode: current.CompletedAtUtc is null ? 202 : 200);
    }
}
