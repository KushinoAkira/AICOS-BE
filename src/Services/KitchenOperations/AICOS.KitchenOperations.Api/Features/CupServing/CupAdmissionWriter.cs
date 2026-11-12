using AICOS.BuildingBlocks.Exceptions;
using AICOS.Contracts.KitchenAdmission;
using AICOS.Contracts.Serving;
using AICOS.KitchenOperations.Api.Domain.Entities;
using AICOS.KitchenOperations.Api.Features.KitchenAdmission;
using AICOS.KitchenOperations.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.KitchenOperations.Api.Features.CupServing;

public sealed class CupAdmissionWriter(KitchenOperationsDbContext db, TimeProvider clock)
{
    public async Task<AdmissionAck> ApplyAsync(CupAdmissionRequest r, CancellationToken ct = default)
    {
        var m = r.Manifest;
        if (new[] { r.EventId, r.TenantId, r.BranchId, r.OrderId }.Contains(Guid.Empty) || m is null
            || m.FulfillmentMode != FulfillmentModes.CupV1 || string.IsNullOrWhiteSpace(m.OrderCode) || m.OrderCode.Length > 40
            || m.Items is null || m.Items.Count is < 1 or > 50 || m.Items.Any(i => i is null || i.OrderItemId == Guid.Empty
                || i.ProductId == Guid.Empty || string.IsNullOrWhiteSpace(i.ProductName) || i.ProductName.Length > 200
                || i.Quantity is < 1 or > 99 || i.Note?.Length > 300)
            || m.Items.Select(i => i.OrderItemId).Distinct().Count() != m.Items.Count || r.Source is null
            || r.Source.CreatedAtUtc.Kind != DateTimeKind.Utc || r.Source.TableNumber?.Length > 20
            || r.Source.OrderType is not ("DineIn" or "Takeaway")
            || r.Source.Status is not ("Pending" or "Confirmed" or "Preparing" or "Ready" or "Completed" or "Cancelled" or "NoShow" or "Unfulfillable"))
            throw new ValidationFailedException("admission", "Invalid CupV1 source manifest.");
        var hash = CupAdmissionProtocol.Hash(r.TenantId, r.BranchId, r.OrderId, m);
        if (hash != r.ManifestHash) throw new ConflictException("Manifest hash mismatch.", "admission.conflict");
        return await KitchenTicketWrites.RunAsync(db, r.TenantId, r.OrderId, async () =>
        {
            if (db.Database.IsRelational())
            {
                var key = $"aicos:admission-receipt:{r.EventId}";
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);
            }
            var receipt = await db.AdmissionReceipts.SingleOrDefaultAsync(x => x.ProducerKey == AdmissionProtocol.Producer
                && x.ConsumerKey == CupAdmissionProtocol.Consumer && x.EventId == r.EventId, ct);
            if (receipt is not null && (receipt.TenantId != r.TenantId || receipt.BranchId != r.BranchId
                || receipt.OrderId != r.OrderId || Convert.ToHexString(receipt.CreationSha256) != hash))
                throw new ConflictException("Admission event identity changed.", "admission.conflict");
            var ticket = await TicketLookup.ByOrderAsync(db, r.TenantId, r.OrderId, ct);
            if (receipt is not null && ticket?.Id != receipt.TicketId)
                throw new ConflictException("Receipt/ticket identity requires review.", "admission.integrity");
            var created = ticket is null;
            var now = clock.GetUtcNow().UtcDateTime;
            if (ticket is null)
            {
                if (receipt is not null) throw new ConflictException("Receipt lost its ticket.", "admission.integrity");
                ticket = new KitchenTicket(r.TenantId, r.OrderId, m.OrderCode, r.BranchId, r.Source.TableNumber,
                    r.Source.OrderType, r.Source.IsPaid, r.Source.PickupSlotStartUtc, r.Source.PayFirst);
                ticket.InitializeCups(m.TableId, m.SessionId);
                db.Tickets.Add(ticket);
                var index = 0; var total = m.Items.Sum(i => i.Quantity);
                foreach (var line in m.Items.OrderBy(i => i.OrderItemId))
                {
                    var item = ticket.AddCupItem(line.OrderItemId, line.ProductId, line.ProductName, line.Quantity, line.Note);
                    for (var unit = 1; unit <= line.Quantity; unit++)
                    {
                        var cup = new CupTask(CupIdentity.Create(r.TenantId, r.OrderId, line.OrderItemId, unit),
                            r.TenantId, r.BranchId, r.OrderId, ticket.Id, item.Id, line.OrderItemId, unit, ++index, now);
                        db.CupTasks.Add(cup); ticket.AddCupSticker(cup, item, total, now);
                    }
                }
                // Historical status does not prove the custody/preparation of individual cups.
                if (r.Source.Status is not ("Pending" or "Confirmed") || r.Source.DeliveredAtUtc is not null
                    || r.Source.CancelledAtUtc is not null) ticket.HoldRecovery("cup.historical_state_requires_review");
            }
            else
            {
                if (ticket.FulfillmentMode != FulfillmentModes.CupV1 || ticket.BranchId != r.BranchId
                    || ticket.SourceTableId != m.TableId || ticket.SourceSessionId != m.SessionId)
                    throw new ConflictException("Never upgrade or reset a legacy ticket.", "admission.conflict");
                var stored = new CupManifest(ticket.OrderCode, ticket.FulfillmentMode, ticket.SourceTableId,
                    ticket.SourceSessionId, ticket.Items.Select(i => new CupAdmissionItem(i.OrderItemId ?? Guid.Empty,
                        i.ProductId, i.ProductName, i.Quantity, i.Note)).ToArray());
                if (CupAdmissionProtocol.Hash(r.TenantId, r.BranchId, r.OrderId, stored) != hash)
                    throw new ConflictException("Ticket manifest differs.", "admission.conflict");
                var cups = await db.CupTasks.Where(c => c.TicketId == ticket.Id).ToListAsync(ct);
                if (cups.Count != m.Items.Sum(i => i.Quantity) || ticket.Stickers.Count != cups.Count
                    || !cups.Select(c => c.OrderCupIndex).Order().SequenceEqual(Enumerable.Range(1, cups.Count))
                    || ticket.Items.Any(i => !cups.Where(c => c.TicketItemId == i.Id && c.OrderItemId == i.OrderItemId)
                        .Select(c => c.UnitIndex).Order().SequenceEqual(Enumerable.Range(1, i.Quantity)))
                    || cups.Any(c => c.TenantId != r.TenantId || c.BranchId != r.BranchId || c.OrderId != r.OrderId
                        || c.Id != CupIdentity.Create(r.TenantId, r.OrderId, c.OrderItemId, c.UnitIndex)
                        || !ticket.Stickers.Any(s => s.CupTaskId == c.Id && s.CupIndex == c.OrderCupIndex
                            && s.TicketId == ticket.Id && s.OrderId == r.OrderId)))
                    throw new ConflictException("Cup/sticker set requires review.", "admission.integrity");
                // Replay verifies immutable admission only; never reset prep, custody or hold.
            }
            if (r.Source.Status is "Cancelled" or "NoShow" or "Unfulfillable" || r.Source.CancelledAtUtc is not null
                || r.Source.DeliveredAtUtc is not null && ticket.Status != Domain.Enums.KitchenTicketStatus.Completed
                || ticket.OrderType != r.Source.OrderType || ticket.TableNumber != r.Source.TableNumber
                || ticket.PayFirst != r.Source.PayFirst)
                ticket.HoldRecovery("cup.source_state_requires_review");
            // Authoritative payment fact is monotonic eligibility only, never a settlement command.
            if (r.Source.IsPaid && !ticket.IsPaid) ticket.MarkAsPaid();
            // A payment can overtake V2 admission, just as it can overtake legacy V1.
            if (created)
            {
                var early = await db.EarlyPayments.SingleOrDefaultAsync(p => p.TenantId == r.TenantId && p.OrderId == r.OrderId, ct);
                if (early is not null)
                {
                    if (!ticket.IsPaid) ticket.MarkAsPaid();
                    db.EarlyPayments.Remove(early);
                }
            }
            if (receipt is null)
            {
                receipt = new AdmissionReceipt
                {
                    ConsumerKey = CupAdmissionProtocol.Consumer,
                    EventId = r.EventId,
                    TenantId = r.TenantId,
                    BranchId = r.BranchId,
                    OrderId = r.OrderId,
                    TicketId = ticket.Id,
                    CreationSha256 = Convert.FromHexString(hash),
                    RecordedAtUtc = now
                };
                db.AdmissionReceipts.Add(receipt);
            }
            receipt.ReceiptStatus = ticket.RecoveryHoldReason is null ? "APPLIED" : "NEEDS_REVIEW";
            receipt.AppliedAtUtc = ticket.RecoveryHoldReason is null ? receipt.AppliedAtUtc ?? now : null;
            receipt.LastVerifiedAtUtc = now; receipt.LastErrorCode = ticket.RecoveryHoldReason;
            await db.SaveChangesAsync(ct);
            return new AdmissionAck(r.EventId, CupAdmissionProtocol.Consumer, r.TenantId, r.BranchId, r.OrderId,
                ticket.Id, created ? "Created" : "AlreadyPresent", ticket.RecoveryHoldReason is null ? "Applied" : "NeedsReview");
        }, ct);
    }
}
