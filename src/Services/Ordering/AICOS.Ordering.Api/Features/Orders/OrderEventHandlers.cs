using AICOS.BuildingBlocks.Messaging;
using AICOS.Contracts.Events;
using AICOS.Ordering.Api.Domain.Entities;
using AICOS.Ordering.Api.Domain.Enums;
using AICOS.Ordering.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.Ordering.Api.Features.Orders;

/// <summary>
/// What other services tell the Order service. Every handler is idempotent (the event bus may deliver twice), looks the order up by
/// tenant and id, and ignores an order it does not know.
/// </summary>
internal static class OrderLookup
{
    public static Task<Order?> FindAsync(OrderingDbContext db, Guid tenantId, Guid orderId, CancellationToken ct) =>
        db.Orders.Include(o => o.Items).AsSplitQuery().FirstOrDefaultAsync(o => o.Id == orderId && o.TenantId == tenantId, ct);
}

/// <summary>Payment confirmed the money (card, wallet, transfer or cash at the counter): the order is paid.</summary>
public sealed class PaymentSucceededHandler(OrderingDbContext db, OrderEventPublisher events) : IIntegrationEventHandler<PaymentSucceededEvent>
{
    public async Task HandleAsync(PaymentSucceededEvent @event, CancellationToken cancellationToken)
    {
        var order = await OrderLookup.FindAsync(db, @event.TenantId, @event.OrderId, cancellationToken);
        if (order is null || order.IsPaid)
        {
            return;
        }

        var wasCompleted = order.Status == OrderStatus.Completed;
        order.MarkAsPaid(OrderAccess.MethodOf(@event.Method), @event.Method, @event.PaymentId, @event.PaidAtUtc);
        await db.SaveChangesAsync(cancellationToken);

        if (order.Status != OrderStatus.Cancelled)
        {
            await events.PaidAsync(order, cancellationToken);   // loyalty points are awarded from this; a late payment for a cancelled order earns nothing
        }

        if (!wasCompleted && order.Status == OrderStatus.Completed)
        {
            await events.CompletedAsync(order, cancellationToken);
        }
    }
}

/// <summary>An online payment ran out of time: an unpaid order is released (BR-25). A plain decline leaves the order open for another try.</summary>
public sealed class PaymentFailedHandler(OrderingDbContext db, OrderEventPublisher events, TimeProvider clock) : IIntegrationEventHandler<PaymentFailedEvent>
{
    public async Task HandleAsync(PaymentFailedEvent @event, CancellationToken cancellationToken)
    {
        if (!@event.TimedOut)
        {
            return;
        }

        var order = await OrderLookup.FindAsync(db, @event.TenantId, @event.OrderId, cancellationToken);
        if (order is null || !order.CancelBecausePaymentTimedOut(clock.GetUtcNow().UtcDateTime))
        {
            return;
        }

        await db.SaveChangesAsync(cancellationToken);
        await events.CancelledAsync(order, wasPaid: false, cancellationToken);
    }
}

public sealed class PaymentRefundedHandler(OrderingDbContext db) : IIntegrationEventHandler<PaymentRefundedEvent>
{
    public async Task HandleAsync(PaymentRefundedEvent @event, CancellationToken cancellationToken)
    {
        var order = await OrderLookup.FindAsync(db, @event.TenantId, @event.OrderId, cancellationToken);
        if (order is null)
        {
            return;
        }

        order.ApplyRefund(@event.TotalRefunded);
        await db.SaveChangesAsync(cancellationToken);
    }
}

public sealed class PaymentRefundFailedHandler(OrderingDbContext db) : IIntegrationEventHandler<PaymentRefundFailedEvent>
{
    public async Task HandleAsync(PaymentRefundFailedEvent @event, CancellationToken cancellationToken)
    {
        var order = await OrderLookup.FindAsync(db, @event.TenantId, @event.OrderId, cancellationToken);
        if (order is null)
        {
            return;
        }

        order.RefundFailed();
        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>The bar started an order.</summary>
public sealed class KitchenOrderStartedHandler(OrderingDbContext db) : IIntegrationEventHandler<KitchenOrderStartedIntegrationEvent>
{
    public async Task HandleAsync(KitchenOrderStartedIntegrationEvent @event, CancellationToken cancellationToken)
    {
        var order = await OrderLookup.FindAsync(db, @event.TenantId, @event.OrderId, cancellationToken);
        if (order is null)
        {
            return;
        }

        order.MarkAsPreparing();
        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>The bar finished an order: it is Ready, and cashiers see it (SCRUM-58).</summary>
public sealed class KitchenOrderReadyHandler(OrderingDbContext db) : IIntegrationEventHandler<KitchenOrderReadyIntegrationEvent>
{
    public async Task HandleAsync(KitchenOrderReadyIntegrationEvent @event, CancellationToken cancellationToken)
    {
        var order = await OrderLookup.FindAsync(db, @event.TenantId, @event.OrderId, cancellationToken);
        if (order is null)
        {
            return;
        }

        order.MarkAsReady(@event.ReadyAtUtc);
        await db.SaveChangesAsync(cancellationToken);
    }
}
