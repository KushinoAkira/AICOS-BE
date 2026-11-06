using AICOS.Contracts.KitchenAdmission;
using AICOS.Ordering.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.Ordering.Api.Features.KitchenAdmission;

public sealed class KitchenAdmissionReconciler(OrderingDbContext db, IKitchenAdmissionTransport transport, TimeProvider clock, ILogger<KitchenAdmissionReconciler> logger)
{
    public async Task<int> ReconcileOnceAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var state = await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var cycle = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ordering.kitchen_admission_reconcile_state (consumer_key, cycle_id, cycle_started_at_utc) VALUES ({AdmissionProtocol.Consumer}, {cycle}, {now}) ON CONFLICT (consumer_key) DO NOTHING", ct);
            var states = await db.AdmissionReconcile.FromSqlInterpolated($"SELECT * FROM ordering.kitchen_admission_reconcile_state WHERE consumer_key = {AdmissionProtocol.Consumer} AND (lease_until_utc IS NULL OR lease_until_utc <= {now}) FOR UPDATE SKIP LOCKED").ToListAsync(ct);
            var current = states.SingleOrDefault();
            if (current is not null)
            {
                current.LeaseToken = Guid.NewGuid(); current.LeaseUntilUtc = now.AddMinutes(2);
                if (current.UpperOrderId is null)
                {
                    current.UpperOrderId = await db.Orders.OrderByDescending(o => o.Id).Select(o => (Guid?)o.Id).FirstOrDefaultAsync(ct);
                    current.LastOrderId = null; current.CycleId = Guid.NewGuid(); current.CycleStartedAtUtc = now;
                }
                await db.SaveChangesAsync(ct);
            }
            await tx.CommitAsync(ct); return current;
        });
        db.ChangeTracker.Clear();
        if (state is null) return 0;
        var orders = state.UpperOrderId is null ? [] : await db.Orders.FromSqlInterpolated($"SELECT * FROM ordering.orders WHERE ({state.LastOrderId}::uuid IS NULL OR id > {state.LastOrderId}::uuid) AND id <= {state.UpperOrderId}::uuid ORDER BY id LIMIT 100")
            .AsNoTracking().Include(o => o.Items).ThenInclude(i => i.Options).AsSplitQuery().OrderBy(o => o.Id).ToListAsync(ct);
        foreach (var order in orders)
        {
            var row = AdmissionProjection.New(order, now, "RECONCILE");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ordering.kitchen_admission_outbox (event_id, tenant_id, branch_id, order_id, consumer_key, schema_version, creation_payload, creation_sha256, origin, state, created_at_utc, next_attempt_at_utc, attempt_count) VALUES ({row.EventId}, {row.TenantId}, {row.BranchId}, {row.OrderId}, {row.ConsumerKey}, 1, {row.CreationPayload}::jsonb, {row.CreationSha256}, 'RECONCILE', 'PENDING', {now}, {now}, 0) ON CONFLICT (tenant_id, order_id, consumer_key) DO NOTHING", ct);
        }
        var ids = orders.Select(o => o.Id).ToArray();
        var rows = await db.AdmissionOutbox.AsNoTracking().Where(r => ids.Contains(r.OrderId) && r.ConsumerKey == AdmissionProtocol.Consumer
            && (r.State == "ACKED" || r.State == "NEEDS_REVIEW")).ToListAsync(ct);
        foreach (var scope in rows.GroupBy(r => new { r.TenantId, r.BranchId }))
        {
            if (scope.Key.TenantId == Guid.Empty || scope.Key.BranchId == Guid.Empty) continue;
            var leaseNow = clock.GetUtcNow().UtcDateTime;
            if (await db.AdmissionReconcile.Where(s => s.ConsumerKey == state.ConsumerKey && s.LeaseToken == state.LeaseToken && s.LeaseUntilUtc > leaseNow)
                .ExecuteUpdateAsync(set => set.SetProperty(s => s.LeaseUntilUtc, leaseNow.AddMinutes(2)), ct) != 1) return 0;
            IReadOnlyList<AdmissionPresence> presence;
            try { presence = await transport.LookupAsync(scope.Key.TenantId, scope.Key.BranchId, scope.Select(r => r.OrderId).ToArray(), ct); }
            catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                if (ct.IsCancellationRequested) throw;
                logger.LogWarning("Kitchen receipt lookup deferred ({Type}); captured admissions remain durable and the sweep can advance.", failure.GetType().Name);
                continue;
            }
            foreach (var row in scope)
            {
                var order = orders.Single(o => o.Id == row.OrderId);
                var target = presence.Single(p => p.OrderId == row.OrderId);
                if (target.Hold == "admission.terminal_conflict") continue;
                var missing = target.TicketId is null && row.State == "ACKED";
                var terminalMismatch = (order.DeliveredAtUtc is not null && target.Status != "Completed")
                    || (order.IsVoided && target.Status != "Cancelled")
                    || ((order.DeliveredAtUtc is not null || order.IsVoided) && target.Hold is not null);
                if (!missing && !terminalMismatch && !(target.Hold is not null && row.State == "ACKED")) continue;
                await db.AdmissionOutbox.Where(r => r.EventId == row.EventId && r.State == row.State)
                    .ExecuteUpdateAsync(set => set.SetProperty(r => r.State, "PENDING").SetProperty(r => r.NextAttemptAtUtc, leaseNow)
                        .SetProperty(r => r.AcknowledgedAtUtc, (DateTime?)null).SetProperty(r => r.AcknowledgedTicketId, (Guid?)null)
                        .SetProperty(r => r.LeaseToken, (Guid?)null).SetProperty(r => r.LeaseUntilUtc, (DateTime?)null), ct);
            }
        }
        var finished = orders.Count < 100 || orders.LastOrDefault()?.Id == state.UpperOrderId;
        Guid? last = finished ? null : orders[^1].Id;
        Guid? upper = finished ? null : state.UpperOrderId;
        DateTime? completed = finished ? clock.GetUtcNow().UtcDateTime : state.LastCompletedAtUtc;
        await db.AdmissionReconcile.Where(s => s.ConsumerKey == state.ConsumerKey && s.LeaseToken == state.LeaseToken)
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.LastOrderId, last).SetProperty(s => s.UpperOrderId, upper)
                .SetProperty(s => s.LastCompletedAtUtc, completed).SetProperty(s => s.LeaseToken, (Guid?)null)
                .SetProperty(s => s.LeaseUntilUtc, (DateTime?)null), ct);
        return orders.Count;
    }
}
