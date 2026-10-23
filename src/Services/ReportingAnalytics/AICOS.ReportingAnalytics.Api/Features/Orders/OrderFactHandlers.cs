using AICOS.BuildingBlocks.Messaging;
using AICOS.Contracts.Events;
using AICOS.ReportingAnalytics.Api.Domain;
using AICOS.ReportingAnalytics.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.ReportingAnalytics.Api.Features.Orders;

/// <summary>Finds the fact of an order or starts it. A message of another brand for the same id, or without a brand, is ignored.</summary>
internal static class Facts
{
    public static async Task<OrderFact?> GetOrCreateAsync(ReportingAnalyticsDbContext db, Guid tenantId, Guid branchId, Guid orderId, CancellationToken ct)
    {
        if (tenantId == Guid.Empty)
        {
            return null;
        }

        var fact = await db.OrderFacts.FirstOrDefaultAsync(f => f.Id == orderId, ct);
        if (fact is not null)
        {
            return fact.TenantId == tenantId ? fact : null;
        }

        fact = new OrderFact(orderId, tenantId, branchId);
        db.OrderFacts.Add(fact);
        return fact;
    }
}

public sealed class ReportOrderCreatedHandler(ReportingAnalyticsDbContext db) : IIntegrationEventHandler<OrderCreatedIntegrationEvent>
{
    public async Task HandleAsync(OrderCreatedIntegrationEvent @event, CancellationToken cancellationToken)
    {
        var fact = await Facts.GetOrCreateAsync(db, @event.TenantId, @event.BranchId, @event.OrderId, cancellationToken);
        if (fact is null)
        {
            return;
        }

        fact.Placed(@event.OrderType, @event.TotalAmount, @event.CreatedAtUtc);
        await db.SaveChangesAsync(cancellationToken);
    }
}

public sealed class ReportPaymentSucceededHandler(ReportingAnalyticsDbContext db) : IIntegrationEventHandler<PaymentSucceededEvent>
{
    public async Task HandleAsync(PaymentSucceededEvent @event, CancellationToken cancellationToken)
    {
        var fact = await Facts.GetOrCreateAsync(db, @event.TenantId, @event.BranchId, @event.OrderId, cancellationToken);
        if (fact is null)
        {
            return;
        }

        fact.Paid(@event.Method, @event.Amount, @event.PaidAtUtc);
        await db.SaveChangesAsync(cancellationToken);
    }
}

public sealed class ReportPaymentRefundedHandler(ReportingAnalyticsDbContext db) : IIntegrationEventHandler<PaymentRefundedEvent>
{
    public async Task HandleAsync(PaymentRefundedEvent @event, CancellationToken cancellationToken)
    {
        var fact = await Facts.GetOrCreateAsync(db, @event.TenantId, @event.BranchId, @event.OrderId, cancellationToken);
        if (fact is null)
        {
            return;
        }

        fact.Refunded(@event.TotalRefunded);
        await db.SaveChangesAsync(cancellationToken);
    }
}

public sealed class ReportOrderCancelledHandler(ReportingAnalyticsDbContext db) : IIntegrationEventHandler<OrderCancelledIntegrationEvent>
{
    public async Task HandleAsync(OrderCancelledIntegrationEvent @event, CancellationToken cancellationToken)
    {
        var fact = await Facts.GetOrCreateAsync(db, @event.TenantId, @event.BranchId, @event.OrderId, cancellationToken);
        if (fact is null)
        {
            return;
        }

        fact.Cancelled(@event.CancelledAtUtc);
        await db.SaveChangesAsync(cancellationToken);
    }
}

public sealed class ReportOrderCompletedHandler(ReportingAnalyticsDbContext db) : IIntegrationEventHandler<OrderCompletedIntegrationEvent>
{
    public async Task HandleAsync(OrderCompletedIntegrationEvent @event, CancellationToken cancellationToken)
    {
        var fact = await Facts.GetOrCreateAsync(db, @event.TenantId, @event.BranchId, @event.OrderId, cancellationToken);
        if (fact is null)
        {
            return;
        }

        fact.Completed(@event.CompletedAtUtc);
        await db.SaveChangesAsync(cancellationToken);
    }
}
