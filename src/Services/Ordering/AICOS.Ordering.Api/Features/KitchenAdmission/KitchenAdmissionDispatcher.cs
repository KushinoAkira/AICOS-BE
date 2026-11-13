using System.Data;
using AICOS.Contracts.KitchenAdmission;
using AICOS.Ordering.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.Ordering.Api.Features.KitchenAdmission;

public sealed class KitchenAdmissionDispatcher(OrderingDbContext db, IKitchenAdmissionTransport transport,
    TimeProvider clock, ILogger<KitchenAdmissionDispatcher> logger, IConfiguration configuration)
{
    public async Task<int> DispatchOnceAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var cups = configuration.GetValue<bool>("CupServing:AdmissionDispatchEnabled");
        var claimed = await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var rows = await db.AdmissionOutbox.FromSqlInterpolated($"SELECT * FROM ordering.kitchen_admission_outbox WHERE (consumer_key = {AdmissionProtocol.Consumer} OR ({cups} AND consumer_key = {AICOS.Contracts.Serving.CupAdmissionProtocol.Consumer})) AND ((state = 'PENDING' AND next_attempt_at_utc <= {now}) OR (state = 'IN_FLIGHT' AND lease_until_utc <= {now})) ORDER BY next_attempt_at_utc, event_id LIMIT 50 FOR UPDATE SKIP LOCKED").ToListAsync(ct);
            foreach (var row in rows)
            {
                row.State = "IN_FLIGHT"; row.LeaseToken = Guid.NewGuid(); row.LeaseUntilUtc = now.AddSeconds(30);
                row.AttemptCount++; row.AcknowledgedAtUtc = null; row.AcknowledgedTicketId = null;
            }
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            return rows;
        });
        db.ChangeTracker.Clear();
        var acknowledged = 0;
        foreach (var row in claimed)
        {
            ct.ThrowIfCancellationRequested();
            var leaseNow = clock.GetUtcNow().UtcDateTime;
            if (await db.AdmissionOutbox.Where(r => r.EventId == row.EventId && r.State == "IN_FLIGHT"
                && r.LeaseToken == row.LeaseToken && r.LeaseUntilUtc > leaseNow)
                .ExecuteUpdateAsync(set => set.SetProperty(r => r.LeaseUntilUtc, leaseNow.AddSeconds(30)), ct) != 1) continue;
            string state = "PENDING", error = "admission.transport";
            Guid? ticket = null;
            try
            {
                var request = await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
                {
                    await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
                    var order = await db.Orders.AsNoTracking().Include(o => o.Items).ThenInclude(i => i.Options).AsSplitQuery()
                        .SingleOrDefaultAsync(o => o.TenantId == row.TenantId && o.BranchId == row.BranchId && o.Id == row.OrderId, ct);
                    await snapshot.CommitAsync(ct); return order;
                });
                if (request is null) { state = "NEEDS_REVIEW"; error = "admission.source_missing"; }
                else
                {
                    // No database transaction is open while waiting for Kitchen.
                    var result = row.SchemaVersion == 2
                        ? await transport.SendCupAsync(AdmissionProjection.CupRequest(row, request), ct)
                        : await transport.SendAsync(AdmissionProjection.Request(row, request), ct);
                    var ack = result.Ack;
                    var valid = ack is not null && ack.EventId == row.EventId && ack.ConsumerKey == row.ConsumerKey
                        && ack.TenantId == row.TenantId && ack.BranchId == row.BranchId && ack.OrderId == row.OrderId
                        && ack.TicketId != Guid.Empty && ack.Result is "Created" or "AlreadyPresent";
                    if (result.Status is 200 or 201 && valid && ack!.ReceiptStatus == "Applied")
                    { state = "ACKED"; error = ""; ticket = ack.TicketId; }
                    else if (result.Status == 202 && valid && ack!.ReceiptStatus == "NeedsReview")
                    { state = "NEEDS_REVIEW"; error = "admission.recovery_hold"; }
                    else if (result.Status is 400 or 409 or 422)
                    { state = "NEEDS_REVIEW"; error = "admission.invalid_or_conflicting"; }
                    else error = result.Status is 401 or 403 ? "admission.authentication" : "admission.invalid_ack_or_transport";
                }
            }
            catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                if (ct.IsCancellationRequested) throw;
            }
            var changedAt = clock.GetUtcNow().UtcDateTime;
            var retryAt = changedAt.AddSeconds(Math.Min(60, Math.Pow(2, Math.Min(row.AttemptCount, 5))) + Random.Shared.NextDouble());
            DateTime? ackAt = state == "ACKED" ? changedAt : null;
            var updated = await db.AdmissionOutbox.Where(r => r.EventId == row.EventId && r.State == "IN_FLIGHT" && r.LeaseToken == row.LeaseToken)
                .ExecuteUpdateAsync(set => set.SetProperty(r => r.State, state).SetProperty(r => r.LeaseToken, (Guid?)null)
                    .SetProperty(r => r.LeaseUntilUtc, (DateTime?)null).SetProperty(r => r.NextAttemptAtUtc, retryAt)
                    .SetProperty(r => r.AcknowledgedAtUtc, ackAt).SetProperty(r => r.AcknowledgedTicketId, ticket)
                    .SetProperty(r => r.LastErrorCode, error.Length == 0 ? null : error), ct);
            if (updated == 1 && state == "ACKED") acknowledged++;
            if (state != "ACKED") logger.LogWarning("Admission {EventId} remains {State}: {Code}", row.EventId, state, error);
        }
        return acknowledged;
    }
}
