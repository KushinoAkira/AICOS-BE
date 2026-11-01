using AICOS.BuildingBlocks.Messaging;
using AICOS.Contracts.Events;
using AICOS.KitchenOperations.Api.Domain.Entities;
using AICOS.KitchenOperations.Api.Persistence;
using AICOS.KitchenOperations.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace AICOS.KitchenOperations.Api.Features;

internal static class TicketLookup
{
    public static Task<KitchenTicket?> ByOrderAsync(KitchenOperationsDbContext db, Guid tenantId, Guid orderId, CancellationToken ct) =>
        db.Tickets.Include(t => t.Items).Include(t => t.Stickers).AsSplitQuery().FirstOrDefaultAsync(t => t.TenantId == tenantId && t.OrderId == orderId, ct);

    /// <summary>Shows a ticket to the bar: the new ticket and, once eligible, its cup stickers.</summary>
    public static async Task AnnounceAsync(KitchenTicket ticket, IKitchenNotifier notifier, TimeProvider clock, KitchenOperationsDbContext db, ILogger logger, CancellationToken ct)
    {
        if (!ticket.IsEligibleForBar)
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
public sealed class OrderCreatedHandler(KitchenOperationsDbContext db, IKitchenNotifier notifier, TimeProvider clock, ILogger<OrderCreatedHandler> logger)
    : IIntegrationEventHandler<OrderCreatedIntegrationEvent>
{
    public async Task HandleAsync(OrderCreatedIntegrationEvent @event, CancellationToken cancellationToken)
    {
        if (@event.TenantId == Guid.Empty)
        {
            logger.LogWarning("An order without a brand (the Guest QR flow) was ignored by the bar queue.");
            return;
        }

        if (await db.Tickets.AnyAsync(t => t.TenantId == @event.TenantId && t.OrderId == @event.OrderId, cancellationToken))
        {
            return;
        }

        var ticket = new KitchenTicket(@event.TenantId, @event.OrderId, @event.OrderCode, @event.BranchId, @event.TableNumber, @event.OrderType, @event.IsPaid, @event.ScheduledPickupTimeUtc);
        foreach (var item in @event.Items)
        {
            ticket.AddItem(item.ProductId, item.ProductName, item.Quantity, item.Note);
        }

        db.Tickets.Add(ticket);
        await db.SaveChangesAsync(cancellationToken);
        await TicketLookup.AnnounceAsync(ticket, notifier, clock, db, logger, cancellationToken);
    }
}

/// <summary>The order was paid: a takeaway ticket reaches the bar now, with its stickers (BR-43).</summary>
public sealed class KitchenPaymentSucceededHandler(KitchenOperationsDbContext db, IKitchenNotifier notifier, TimeProvider clock, ILogger<KitchenPaymentSucceededHandler> logger)
    : IIntegrationEventHandler<PaymentSucceededEvent>
{
    public async Task HandleAsync(PaymentSucceededEvent @event, CancellationToken cancellationToken)
    {
        await notifier.NotifyCounterAsync(@event.TenantId, @event.BranchId, "PaymentSucceeded", new { @event.OrderId, @event.Amount, @event.Method }, cancellationToken);
        var ticket = await TicketLookup.ByOrderAsync(db, @event.TenantId, @event.OrderId, cancellationToken);
        if (ticket is null || ticket.IsPaid)
        {
            return;
        }

        var newlyVisible = ticket.MarkAsPaid();
        await db.SaveChangesAsync(cancellationToken);
        if (newlyVisible)
        {
            await TicketLookup.AnnounceAsync(ticket, notifier, clock, db, logger, cancellationToken);
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
        var ticket = await TicketLookup.ByOrderAsync(db, @event.TenantId, @event.OrderId, cancellationToken);
        if (ticket is null || ticket.Status is Domain.Enums.KitchenTicketStatus.Cancelled or Domain.Enums.KitchenTicketStatus.Completed)
        {
            return;
        }

        ticket.Cancel(clock.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(cancellationToken);
        await KitchenEndpoints.SafeAsync(logger, () => notifier.NotifyStatusChangedAsync(ticket.TenantId, ticket.BranchId, ticket.Id, ticket.Status.ToString(), cancellationToken));
    }
}

/// <summary>The cashier handed the order over: the ticket is closed.</summary>
public sealed class KitchenOrderCompletedHandler(KitchenOperationsDbContext db, IKitchenNotifier notifier, TimeProvider clock, ILogger<KitchenOrderCompletedHandler> logger)
    : IIntegrationEventHandler<OrderCompletedIntegrationEvent>
{
    public async Task HandleAsync(OrderCompletedIntegrationEvent @event, CancellationToken cancellationToken)
    {
        var ticket = await TicketLookup.ByOrderAsync(db, @event.TenantId, @event.OrderId, cancellationToken);
        if (ticket is null || ticket.Status != Domain.Enums.KitchenTicketStatus.ReadyForPickup)
        {
            return;
        }

        ticket.Complete(clock.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(cancellationToken);
        await KitchenEndpoints.SafeAsync(logger, () => notifier.NotifyStatusChangedAsync(ticket.TenantId, ticket.BranchId, ticket.Id, ticket.Status.ToString(), cancellationToken));
    }
}
