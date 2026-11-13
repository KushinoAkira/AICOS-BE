using AICOS.BuildingBlocks.Messaging;
using AICOS.Contracts.Events;
using AICOS.KitchenOperations.Api.Domain.Entities;
using AICOS.KitchenOperations.Api.Persistence;
using AICOS.KitchenOperations.Api.Services;
using Microsoft.EntityFrameworkCore;
using AICOS.Contracts.KitchenAdmission;
using AICOS.KitchenOperations.Api.Features.KitchenAdmission;

namespace AICOS.KitchenOperations.Api.Features;

internal static class TicketLookup
{
    public static Task<KitchenTicket?> ByOrderAsync(KitchenOperationsDbContext db, Guid tenantId, Guid orderId, CancellationToken ct) =>
        db.Tickets.Include(t => t.Items).Include(t => t.Stickers).AsSplitQuery().FirstOrDefaultAsync(t => t.TenantId == tenantId && t.OrderId == orderId, ct);

    /// <summary>Shows a ticket to the bar: the new ticket and, once eligible, its cup stickers.</summary>
    public static async Task AnnounceAsync(KitchenTicket ticket, IKitchenNotifier notifier, TimeProvider clock, KitchenOperationsDbContext db, ILogger logger, CancellationToken ct)
    {
        if (!ticket.IsEligibleForBar || ticket.RecoveryHoldReason is not null)
        {
            return;
        }

        var generated = ticket.TryGenerateCupStickers(clock.GetUtcNow().UtcDateTime, out var stickers);
        if (generated)
        {
            await db.SaveChangesAsync(ct);
        }

        await KitchenEndpoints.SafeAsync(logger, () => notifier.NotifyNewTicketAsync(ticket.TenantId, ticket.BranchId, TicketMapper.ToDto(ticket), ct));
        if (generated && stickers.Count > 0)
        {
            await KitchenEndpoints.SafeAsync(logger, () => notifier.NotifyStickersPrintedAsync(ticket.TenantId, ticket.BranchId, stickers.Select(TicketMapper.ToDto).ToList(), ct));
        }
    }
}

/// <summary>A new order becomes a ticket. A takeaway order waits unseen until it is paid; a dine-in order is shown at once. Idempotent per order.</summary>
public sealed class OrderCreatedHandler(KitchenAdmissionService admission, ILogger<OrderCreatedHandler> logger)
    : IIntegrationEventHandler<OrderCreatedIntegrationEvent>
{
    public async Task HandleAsync(OrderCreatedIntegrationEvent message, CancellationToken ct)
    {
        // V2 owns canonical identity; the additive marker prevents legacy Redis admission racing HTTP.
        if (message.FulfillmentMode != AICOS.Contracts.Serving.FulfillmentModes.WholeOrder) return;
        if (message.TenantId == Guid.Empty)
        {
            logger.LogWarning("An order without a brand was ignored by the bar queue.");
            return;
        }
        var creation = new AdmissionCreation(message.OrderCode, message.Items.Select(i => new AdmissionItem(i.ProductId, i.ProductName, i.Quantity, i.Note)).ToArray());
        await admission.EnsureAsync(new AdmissionRequest(message.EventId, 1, AdmissionProtocol.Consumer,
            message.TenantId, message.BranchId, message.OrderId, creation,
            Convert.ToHexString(AdmissionProtocol.Hash(message.TenantId, message.BranchId, message.OrderId, creation)),
            new AdmissionFacts(message.OrderType, "Pending", message.IsPaid, message.TableNumber, message.CreatedAtUtc,
                message.ScheduledPickupTimeUtc, null, null, message.PayFirst)), false, ct);
    }
}
/// <summary>The order was paid: a takeaway ticket reaches the bar now, with its stickers (BR-43).</summary>
public sealed class KitchenPaymentSucceededHandler(KitchenOperationsDbContext db, IKitchenNotifier notifier, TimeProvider clock, ILogger<KitchenPaymentSucceededHandler> logger)
    : IIntegrationEventHandler<PaymentSucceededEvent>
{
    public async Task HandleAsync(PaymentSucceededEvent @event, CancellationToken cancellationToken)
    {
        await notifier.NotifyCounterAsync(@event.TenantId, @event.BranchId, "PaymentSucceeded", new { @event.OrderId, @event.Amount, @event.Method }, cancellationToken);
        // Admission may have inserted the ticket without committing yet. Share its order lock
        // before looking up the row; an unlocked read would silently lose the payment event.
        var outcome = await KitchenTicketWrites.RunAsync(db, @event.TenantId, @event.OrderId, async () =>
        {
            var ticket = await TicketLookup.ByOrderAsync(db, @event.TenantId, @event.OrderId, cancellationToken);
            if (ticket is null)
            {
                if (!await db.EarlyPayments.AnyAsync(p => p.TenantId == @event.TenantId && p.OrderId == @event.OrderId, cancellationToken))
                {
                    db.EarlyPayments.Add(new EarlyPayment { TenantId = @event.TenantId, OrderId = @event.OrderId,
                        PaidAtUtc = clock.GetUtcNow().UtcDateTime });
                    await db.SaveChangesAsync(cancellationToken);
                }
                return (Ticket: (KitchenTicket?)null, NewlyVisible: false);
            }
            if (ticket.BranchId != @event.BranchId)
                return (Ticket: (KitchenTicket?)null, NewlyVisible: false);
            if (ticket.IsPaid)
            {
                logger.LogInformation("Kitchen payment event for order {OrderId}: ticket {TicketId} already paid", @event.OrderId, ticket.Id);
                return (Ticket: (KitchenTicket?)ticket, NewlyVisible: false);
            }

            var newlyVisible = ticket.MarkAsPaid();
            await db.SaveChangesAsync(cancellationToken);
            return (Ticket: (KitchenTicket?)ticket, NewlyVisible: newlyVisible);
        }, cancellationToken);
        if (outcome.Ticket is not { } committedTicket) return;
        logger.LogInformation("Kitchen payment committed for order {OrderId}: ticket {TicketId}, newly visible {NewlyVisible}, held {Held}, mode {FulfillmentMode}",
            @event.OrderId, committedTicket.Id, outcome.NewlyVisible, committedTicket.RecoveryHoldReason is not null, committedTicket.FulfillmentMode);
        if (outcome.NewlyVisible && committedTicket.FulfillmentMode == AICOS.Contracts.Serving.FulfillmentModes.WholeOrder)
        {
            await TicketLookup.AnnounceAsync(committedTicket, notifier, clock, db, logger, cancellationToken);
        }
    }
}

/// <summary>A table asked for the bill: tell the cashier screens of the branch.</summary>
public sealed class KitchenBillRequestedHandler(IKitchenNotifier notifier) : IIntegrationEventHandler<TableBillRequestedIntegrationEvent>
{
    public Task HandleAsync(TableBillRequestedIntegrationEvent @event, CancellationToken cancellationToken) =>
        notifier.NotifyCounterAsync(@event.TenantId, @event.BranchId, "TableBillRequested", new { @event.SessionId, @event.TableCode, @event.Method }, cancellationToken);
}

public sealed class KitchenOrderCancelledHandler(KitchenOperationsDbContext db, IKitchenNotifier notifier, TimeProvider clock, ILogger<KitchenOrderCancelledHandler> logger)
    : IIntegrationEventHandler<OrderCancelledIntegrationEvent>
{
    public async Task HandleAsync(OrderCancelledIntegrationEvent @event, CancellationToken cancellationToken)
    {
        var ticket = await KitchenTicketWrites.ApplyAsync(db, @event.TenantId, @event.BranchId, @event.OrderId,
            t => t.Status is not (Domain.Enums.KitchenTicketStatus.Cancelled or Domain.Enums.KitchenTicketStatus.Completed),
            t => t.Cancel(clock.GetUtcNow().UtcDateTime), cancellationToken);
        if (ticket is null) return;
        await KitchenEndpoints.SafeAsync(logger, () => notifier.NotifyStatusChangedAsync(ticket.TenantId, ticket.BranchId, ticket.Id, ticket.Status.ToString(), cancellationToken));
    }
}

/// <summary>The cashier handed the order over: the ticket is closed.</summary>
public sealed class KitchenOrderCompletedHandler(KitchenOperationsDbContext db, IKitchenNotifier notifier, TimeProvider clock, ILogger<KitchenOrderCompletedHandler> logger)
    : IIntegrationEventHandler<OrderCompletedIntegrationEvent>
{
    public async Task HandleAsync(OrderCompletedIntegrationEvent @event, CancellationToken cancellationToken)
    {
        var ticket = await KitchenTicketWrites.ApplyAsync(db, @event.TenantId, @event.BranchId, @event.OrderId,
            t => t.Status == Domain.Enums.KitchenTicketStatus.ReadyForPickup,
            t => t.Complete(clock.GetUtcNow().UtcDateTime), cancellationToken);
        if (ticket is null) return;
        await KitchenEndpoints.SafeAsync(logger, () => notifier.NotifyStatusChangedAsync(ticket.TenantId, ticket.BranchId, ticket.Id, ticket.Status.ToString(), cancellationToken));
    }
}
