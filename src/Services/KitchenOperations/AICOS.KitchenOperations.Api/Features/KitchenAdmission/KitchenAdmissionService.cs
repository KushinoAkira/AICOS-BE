using System.Security.Cryptography;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.Contracts.KitchenAdmission;
using AICOS.KitchenOperations.Api.Domain.Entities;
using AICOS.KitchenOperations.Api.Domain.Enums;
using AICOS.KitchenOperations.Api.Persistence;
using AICOS.KitchenOperations.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace AICOS.KitchenOperations.Api.Features.KitchenAdmission;

public sealed class KitchenAdmissionService(KitchenOperationsDbContext db, IKitchenNotifier notifier,
    TimeProvider clock, IConfiguration configuration, ILogger<KitchenAdmissionService> logger)
{
    public async Task<AdmissionAck> EnsureAsync(AdmissionRequest request, bool durable, CancellationToken ct)
    {
        Validate(request);
        var hash = AdmissionProtocol.Hash(request.TenantId, request.BranchId, request.OrderId, request.Creation);
        var now = clock.GetUtcNow().UtcDateTime;
        var receiptEnabled = durable || configuration.GetValue<bool>("KitchenAdmission:ReceiverEnabled");
        var outcome = await KitchenTicketWrites.RunAsync(db, request.TenantId, request.OrderId, async () =>
        {
            AdmissionReceipt? receipt = null;
            if (receiptEnabled)
            {
                if (db.Database.IsRelational())
                {
                    var receiptKey = $"aicos:admission-receipt:{request.EventId}";
                    await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({receiptKey}, 0))", ct);
                }
                receipt = await db.AdmissionReceipts.SingleOrDefaultAsync(r => r.ProducerKey == AdmissionProtocol.Producer
                    && r.ConsumerKey == AdmissionProtocol.Consumer && r.EventId == request.EventId, ct);
                if (receipt is not null && (receipt.TenantId != request.TenantId || receipt.BranchId != request.BranchId
                    || receipt.OrderId != request.OrderId || !CryptographicOperations.FixedTimeEquals(receipt.CreationSha256, hash)))
                    throw new ConflictException("Admission identity or immutable content differs.", "admission.conflict");
            }
            var ticket = await TicketLookup.ByOrderAsync(db, request.TenantId, request.OrderId, ct);
            if (receipt is not null && (ticket is null || ticket.Id != receipt.TicketId))
                throw new ConflictException("Receipt/ticket integrity requires review.", "admission.integrity");
            var created = ticket is null;
            if (ticket is null)
            {
                ticket = new KitchenTicket(request.TenantId, request.OrderId, request.Creation.OrderCode,
                    request.BranchId, request.Source.TableNumber, request.Source.OrderType, request.Source.IsPaid, request.Source.PickupSlotStartUtc);
                foreach (var item in request.Creation.Items) ticket.AddItem(item.ProductId, item.ProductName, item.Quantity, item.Note);
                db.Tickets.Add(ticket);
            }
            else
            {
                var existing = new AdmissionCreation(ticket.OrderCode, ticket.Items.Select(i => new AdmissionItem(i.ProductId, i.ProductName, i.Quantity, i.Note)).ToArray());
                if (ticket.BranchId != request.BranchId || !CryptographicOperations.FixedTimeEquals(
                    AdmissionProtocol.Hash(ticket.TenantId, ticket.BranchId, ticket.OrderId, existing), hash))
                    throw new ConflictException("Ticket does not match the immutable order.", "admission.conflict");
            }
            if (durable) ApplySource(ticket, request.Source, created, now);
            var notify = created && ticket.RecoveryHoldReason is null && ticket.IsEligibleForBar
                && ticket.Status is not (KitchenTicketStatus.Completed or KitchenTicketStatus.Cancelled);
            if (notify) ticket.TryGenerateCupStickers(now, out _);
            if (receiptEnabled)
            {
                if (receipt is null)
                {
                    receipt = new AdmissionReceipt { ConsumerKey = AdmissionProtocol.Consumer, EventId = request.EventId,
                        TenantId = request.TenantId, BranchId = request.BranchId, OrderId = request.OrderId,
                        CreationSha256 = hash, TicketId = ticket.Id, RecordedAtUtc = now };
                    db.AdmissionReceipts.Add(receipt);
                }
                receipt.ReceiptStatus = ticket.RecoveryHoldReason is null ? "APPLIED" : "NEEDS_REVIEW";
                receipt.AppliedAtUtc = ticket.RecoveryHoldReason is null ? receipt.AppliedAtUtc ?? now : null;
                receipt.LastVerifiedAtUtc = now; receipt.LastErrorCode = ticket.RecoveryHoldReason;
            }
            await db.SaveChangesAsync(ct);
            return (Ticket: ticket, Created: created, Notify: notify);
        }, ct);
        if (outcome.Notify)
        {
            await KitchenEndpoints.SafeAsync(logger, () => notifier.NotifyNewTicketAsync(outcome.Ticket.TenantId,
                outcome.Ticket.BranchId, TicketMapper.ToDto(outcome.Ticket), ct));
            if (outcome.Ticket.Stickers.Count > 0)
                await KitchenEndpoints.SafeAsync(logger, () => notifier.NotifyStickersPrintedAsync(outcome.Ticket.TenantId,
                    outcome.Ticket.BranchId, outcome.Ticket.Stickers.Select(TicketMapper.ToDto).ToList(), ct));
        }
        return new AdmissionAck(request.EventId, AdmissionProtocol.Consumer, request.TenantId, request.BranchId,
            request.OrderId, outcome.Ticket.Id, outcome.Created ? "Created" : "AlreadyPresent",
            outcome.Ticket.RecoveryHoldReason is null ? "Applied" : "NeedsReview");
    }

    private static void ApplySource(KitchenTicket ticket, AdmissionFacts facts, bool created, DateTime now)
    {
        var isVoid = facts.Status is "Cancelled" or "NoShow" or "Unfulfillable";
        if (isVoid && facts.DeliveredAtUtc is not null) { ticket.HoldRecovery("admission.terminal_conflict"); return; }
        if (facts.DeliveredAtUtc is { } delivered)
        {
            if (ticket.Status == KitchenTicketStatus.Cancelled) ticket.HoldRecovery("admission.terminal_conflict");
            else { ticket.ConfirmDelivered(delivered); ticket.ClearRecoveryHold(); }
        }
        else if (isVoid)
        {
            if (ticket.Status == KitchenTicketStatus.Completed) ticket.HoldRecovery("admission.terminal_conflict");
            else { if (ticket.Status != KitchenTicketStatus.Cancelled) ticket.Cancel(facts.CancelledAtUtc ?? now); ticket.ClearRecoveryHold(); }
        }
        else if (created && facts.Status is "Preparing" or "Ready" or "Completed") ticket.HoldRecovery("admission.preparation_unknown");
        else if (!created && ticket.OrderType != facts.OrderType) ticket.HoldRecovery("admission.service_type_changed");
    }

    private static void Validate(AdmissionRequest request)
    {
        if (request.EventId == Guid.Empty || request.TenantId == Guid.Empty || request.BranchId == Guid.Empty || request.OrderId == Guid.Empty
            || request.SchemaVersion != 1 || request.ConsumerKey != AdmissionProtocol.Consumer || request.Creation is null || request.Source is null
            || string.IsNullOrWhiteSpace(request.Creation.OrderCode) || request.Creation.OrderCode.Length > 40
            || request.Creation.Items is null || request.Creation.Items.Count is < 1 or > 50
            || request.Creation.Items.Any(i => i is null || i.ProductId == Guid.Empty || string.IsNullOrWhiteSpace(i.ProductName)
                || i.ProductName.Length > 200 || i.Quantity is < 1 or > 99 || i.Note?.Length > 300)
            || request.Source.TableNumber?.Length > 20
            || request.Source.OrderType is not ("DineIn" or "Takeaway" or "Delivery")
            || request.Source.Status is not ("Pending" or "Confirmed" or "Preparing" or "Ready" or "Completed" or "Cancelled" or "NoShow" or "Unfulfillable"))
            throw new ValidationFailedException("admission", "Invalid admission payload/version.");
        byte[] supplied;
        try { supplied = Convert.FromHexString(request.CreationHash); }
        catch (Exception e) when (e is FormatException or ArgumentNullException) { throw new ValidationFailedException("creationHash", "Invalid immutable hash."); }
        if (!CryptographicOperations.FixedTimeEquals(supplied, AdmissionProtocol.Hash(request.TenantId, request.BranchId, request.OrderId, request.Creation)))
            throw new ConflictException("Immutable hash mismatch.", "admission.conflict");
    }
}
