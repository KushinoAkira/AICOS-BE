using AICOS.BuildingBlocks.Exceptions;
using AICOS.Contracts.Serving;
using AICOS.Ordering.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.Ordering.Api.Features.Fulfillment;

/// <summary>Transactional inbox/projection core, not an HTTP receiver or a substitute for machine authentication.</summary>
public sealed class FulfillmentProjectionWriter(OrderingDbContext db, TimeProvider clock)
{
    public Task<FulfillmentAck> ApplyAsync(FulfillmentSnapshot snapshot, CancellationToken ct = default) =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
            if (transaction is not null)
            {
                var key = $"aicos:fulfillment:{snapshot.TenantId}:{snapshot.OrderId}";
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);
                var receiptKey = $"aicos:fulfillment-receipt:{snapshot.EventId}";
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({receiptKey}, 0))", ct);
            }
            var order = await db.Orders.AsNoTracking().Include(o => o.Items).SingleOrDefaultAsync(o => o.Id == snapshot.OrderId
                && o.TenantId == snapshot.TenantId && o.BranchId == snapshot.BranchId, ct)
                ?? throw new NotFoundException("Order", snapshot.OrderId);
            if (order.FulfillmentMode != FulfillmentModes.CupV1 || order.IsVoided)
                throw new ConflictException("Order requires review before applying cup fulfillment.", "fulfillment.order_conflict");
            var sourceManifest = await db.AdmissionOutbox.AsNoTracking().Where(r => r.OrderId == order.Id
                && r.TenantId == snapshot.TenantId && r.ConsumerKey == "kitchen-order-admission-v2" && r.SchemaVersion == 2)
                .Select(r => r.CreationSha256).SingleOrDefaultAsync(ct);
            if (sourceManifest is null || Convert.ToHexString(sourceManifest) != snapshot.ManifestHash
                || order.Items.Sum(i => (long)i.Quantity) != snapshot.Total)
                throw new ConflictException("Snapshot does not match authoritative admission.", "fulfillment.manifest_conflict");
            var hash = FulfillmentSnapshotHash.Compute(snapshot);
            var receipt = await db.FulfillmentReceipts.AsNoTracking().SingleOrDefaultAsync(r => r.EventId == snapshot.EventId, ct);
            if (receipt is not null)
            {
                if (receipt.TenantId != snapshot.TenantId || receipt.BranchId != snapshot.BranchId
                    || receipt.OrderId != snapshot.OrderId || receipt.PayloadHash != hash)
                    throw new ConflictException("Receipt identity differs.", "fulfillment.receipt_conflict");
            }
            var now = transaction is not null
                ? await db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct)
                : clock.GetUtcNow().UtcDateTime;
            var projection = await db.FulfillmentProjection.SingleOrDefaultAsync(p => p.OrderId == snapshot.OrderId, ct);
            var added = projection is null; projection ??= new OrderFulfillmentProjection();
            var result = projection.Apply(snapshot, now).ToString();
            if (added) db.FulfillmentProjection.Add(projection);
            if (receipt is null) db.FulfillmentReceipts.Add(new FulfillmentReceipt
            {
                EventId = snapshot.EventId,
                TenantId = snapshot.TenantId,
                BranchId = snapshot.BranchId,
                OrderId = snapshot.OrderId,
                SourceVersion = snapshot.Version,
                PayloadHash = hash,
                Result = result,
                AppliedAtUtc = now
            });
            await db.SaveChangesAsync(ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
            // Revalidate/repair the projection, but preserve the historical response for this EventId.
            return Ack(snapshot, hash, receipt?.Result ?? result);
        });

    private static FulfillmentAck Ack(FulfillmentSnapshot s, string hash, string result) =>
        new(s.EventId, s.TenantId, s.BranchId, s.OrderId, s.Version, hash, result);
}
