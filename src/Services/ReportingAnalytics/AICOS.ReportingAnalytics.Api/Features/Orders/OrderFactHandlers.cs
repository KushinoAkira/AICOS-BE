using AICOS.BuildingBlocks.Messaging;
using AICOS.Contracts.Events;
using AICOS.ReportingAnalytics.Api.Domain;
using AICOS.ReportingAnalytics.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AICOS.ReportingAnalytics.Api.Features.Orders;

/// <summary>Finds the fact of an order or starts it. A message of another brand for the same id, or without a brand, is ignored.</summary>
internal static class Facts
{
    /// <summary>
    /// Applies one event to the fact of its order. The events of one order (created, paid, cancelled...) arrive on different handlers at the same
    /// moment, so two of them can both try to insert the fact: the loser gets a unique-key error. Events are delivered once, so it must not drop
    /// its change: it reads the fact the winner inserted and applies the change to that.
    /// </summary>
    public static async Task ApplyAsync(ReportingAnalyticsDbContext db, Guid tenantId, Guid branchId, Guid orderId, Action<OrderFact> change, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var fact = await GetOrCreateAsync(db, tenantId, branchId, orderId, ct);
            if (fact is null)
            {
                return;
            }

            change(fact);
            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateException ex) when (attempt < 3 && ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                db.ChangeTracker.Clear();
            }
        }
    }

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
    public Task HandleAsync(OrderCreatedIntegrationEvent @event, CancellationToken cancellationToken) =>
        Facts.ApplyAsync(db, @event.TenantId, @event.BranchId, @event.OrderId, f => f.Placed(@event.OrderType, @event.TotalAmount, @event.CreatedAtUtc), cancellationToken);
}

public sealed class ReportPaymentSucceededHandler(ReportingAnalyticsDbContext db) : IIntegrationEventHandler<PaymentSucceededEvent>
{
    public Task HandleAsync(PaymentSucceededEvent @event, CancellationToken cancellationToken) =>
        Facts.ApplyAsync(db, @event.TenantId, @event.BranchId, @event.OrderId, f => f.Paid(@event.Method, @event.Amount, @event.PaidAtUtc), cancellationToken);
}

public sealed class ReportPaymentRefundedHandler(ReportingAnalyticsDbContext db) : IIntegrationEventHandler<PaymentRefundedEvent>
{
    public Task HandleAsync(PaymentRefundedEvent @event, CancellationToken cancellationToken) =>
        Facts.ApplyAsync(db, @event.TenantId, @event.BranchId, @event.OrderId, f => f.Refunded(@event.TotalRefunded), cancellationToken);
}

public sealed class ReportOrderCancelledHandler(ReportingAnalyticsDbContext db) : IIntegrationEventHandler<OrderCancelledIntegrationEvent>
{
    public Task HandleAsync(OrderCancelledIntegrationEvent @event, CancellationToken cancellationToken) =>
        Facts.ApplyAsync(db, @event.TenantId, @event.BranchId, @event.OrderId, f => f.Cancelled(@event.CancelledAtUtc), cancellationToken);
}

public sealed class ReportOrderCompletedHandler(ReportingAnalyticsDbContext db) : IIntegrationEventHandler<OrderCompletedIntegrationEvent>
{
    public Task HandleAsync(OrderCompletedIntegrationEvent @event, CancellationToken cancellationToken) =>
        Facts.ApplyAsync(db, @event.TenantId, @event.BranchId, @event.OrderId, f => f.Completed(@event.CompletedAtUtc), cancellationToken);
}
