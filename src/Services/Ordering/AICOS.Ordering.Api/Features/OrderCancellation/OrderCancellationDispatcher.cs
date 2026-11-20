using AICOS.BuildingBlocks.Exceptions;
using AICOS.Contracts.Orders;
using AICOS.Ordering.Api.Domain.Entities;
using AICOS.Ordering.Api.Features.Orders;
using AICOS.Ordering.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.Ordering.Api.Features.OrderCancellation;

/// <summary>Persisted stage transitions; HTTP is always outside PostgreSQL transactions.</summary>
public sealed class OrderCancellationDispatcher(OrderingDbContext db, IKitchenCancellationClient kitchen, TimeProvider clock, OrderEventPublisher events)
{
    public async Task TryAsync(Guid id, CancellationToken ct)
    {
        if (!db.CancellationGuardEnabled) return;
        try
        {
            for (var step = 0; step < 4; step++)
            {
                var op = await db.Cancellations.AsNoTracking().SingleAsync(x => x.Id == id, ct);
                if (op.CompletedAtUtc != null)
                {
                    if (op.State == "COMPLETED" && op.LegacyPublishedAtUtc is null) await PublishLegacy(op, ct);
                    return;
                }
                var action = op.State switch { "QUEUED" => "reserve", "APPLIED" => "commit", "ABORTING" => "abort", _ => null };
                CancellationAck? ack = action is null ? null : await kitchen.SendAsync(action, op.Reservation(), ct);
                var progressed = await OrderCancellationRules.Locked(db, op.TenantId, op.OrderId, op.TableId, async () =>
                {
                    var current = await db.Cancellations.SingleAsync(x => x.Id == id, ct);
                    if (current.State != op.State || current.CompletedAtUtc != null) return true;
                    var now = clock.GetUtcNow().UtcDateTime;
                    if (ack is not null && (ack.OperationId != id || ack.TenantId != op.TenantId || ack.BranchId != op.BranchId
                        || ack.OrderId != op.OrderId || ack.RequestHash != CancellationProtocol.Hash(op.Reservation())))
                        throw new ConflictException("Cancellation ACK differs.", "order.cancel_reconciliation");
                    if (current.State == "QUEUED")
                    {
                        if (ack!.State == "WAITING") return false;
                        if (ack.State == "REJECTED") current.Finish("REJECTED", ack.ReasonCode ?? "order.cancel_reconciliation", now);
                        else if (ack.State == "HELD") { current.State = "RESERVED"; current.Version++; current.ReasonCode = null; }
                        else throw new ConflictException("Unexpected cancellation receipt.", "order.cancel_reconciliation");
                    }
                    else if (current.State == "RESERVED")
                    {
                        var order = await db.Orders.Include(x => x.Items).ThenInclude(x => x.Options)
                            .SingleAsync(x => x.Id == current.OrderId && x.TenantId == current.TenantId && x.BranchId == current.BranchId, ct);
                        var session = await db.TableSessions.AsNoTracking().SingleAsync(x => x.Id == current.SessionId && x.TenantId == current.TenantId, ct);
                        if (!session.IsOpen || session.TableId != current.TableId || session.BranchId != current.BranchId || OrderCancellationRules.Blocked(order) != null
                            || order.TableSessionId != current.SessionId || OrderCancellationRules.Fingerprint(order) != current.Fingerprint
                            || await db.SessionCheckouts.AnyAsync(x => x.SessionId == current.SessionId, ct))
                        { current.State = "ABORTING"; current.ReasonCode = "order.cancel_stale"; current.Version++; }
                        else
                        {
                            db.ApplyingCancellation = id;
                            try
                            {
                                order.CancelOrder(current.Reason, now);
                                current.AfterJson = OrderCancellationRules.Snapshot(order); current.State = "APPLIED"; current.Version++;
                                await db.SaveChangesAsync(ct);
                            }
                            finally { db.ApplyingCancellation = null; }
                            return true;
                        }
                    }
                    else if (current.State == "APPLIED" && ack!.State == "CANCELLED") current.Finish("COMPLETED", null, now);
                    else if (current.State == "ABORTING" && ack!.State == "ABORTED") current.Finish("ABORTED", current.ReasonCode, now);
                    else throw new ConflictException("Cancellation needs reconciliation.", "order.cancel_reconciliation");
                    await db.SaveChangesAsync(ct); return true;
                }, ct);
                if (!progressed) break;
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException
            or DependencyUnavailableException or ConflictException && !ct.IsCancellationRequested)
        {
            // Unknown transport outcome never releases a held ticket or changes a financial result.
        }
        var pending = await db.Cancellations.AsNoTracking().SingleAsync(x => x.Id == id, ct);
        if (pending.CompletedAtUtc is null)
            await OrderCancellationRules.Locked(db, pending.TenantId, pending.OrderId, pending.TableId, async () =>
            {
                var current = await db.Cancellations.SingleAsync(x => x.Id == id, ct);
                if (current.CompletedAtUtc is null)
                { current.Attempts++; current.NextAttemptAtUtc = clock.GetUtcNow().UtcDateTime.AddSeconds(Math.Min(60, 2 + current.Attempts * 2)); await db.SaveChangesAsync(ct); }
                return true;
            }, ct);
    }

    private async Task PublishLegacy(OrderCancellationOperation op, CancellationToken ct)
    {
        var order = await db.Orders.Include(x => x.Items).ThenInclude(x => x.Options).SingleAsync(x => x.Id == op.OrderId && x.TenantId == op.TenantId, ct);
        await events.CancelledAsync(order, false, ct); // Compatibility hint only; ticket cancellation uses the durable HTTP protocol.
        await OrderCancellationRules.Locked(db, op.TenantId, op.OrderId, op.TableId, async () =>
        {
            var current = await db.Cancellations.SingleAsync(x => x.Id == op.Id, ct);
            current.LegacyPublishedAtUtc ??= clock.GetUtcNow().UtcDateTime; await db.SaveChangesAsync(ct); return true;
        }, ct);
    }
}

public sealed class OrderCancellationWorker(IServiceScopeFactory scopes, IConfiguration config, ILogger<OrderCancellationWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (config.GetValue<bool>("OrderCancellation:DispatcherEnabled") && config.GetValue<bool>("OrderCancellation:GuardEnabled"))
                {
                    using var scope = scopes.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
                    var now = DateTime.UtcNow;
                    var ids = await db.Cancellations.AsNoTracking().Where(x => x.NextAttemptAtUtc <= now &&
                        (x.CompletedAtUtc == null || x.State == "COMPLETED" && x.LegacyPublishedAtUtc == null))
                        .OrderBy(x => x.NextAttemptAtUtc).Select(x => x.Id).Take(20).ToListAsync(ct);
                    foreach (var id in ids) await scope.ServiceProvider.GetRequiredService<OrderCancellationDispatcher>().TryAsync(id, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) { log.LogWarning("Cancellation recovery will retry ({Type}).", e.GetType().Name); }
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }
}
