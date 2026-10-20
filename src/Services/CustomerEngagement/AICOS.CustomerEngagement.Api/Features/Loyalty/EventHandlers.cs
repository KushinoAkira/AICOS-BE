using AICOS.BuildingBlocks.Messaging;
using AICOS.Contracts.Events;
using AICOS.CustomerEngagement.Api.Domain;
using AICOS.CustomerEngagement.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.CustomerEngagement.Api.Features.Loyalty;

/// <summary>Loyalty settings, section "Loyalty".</summary>
public sealed class LoyaltyOptions
{
    /// <summary>BR-25: 1 point for every this much money spent on a completed order (default 10,000 VND).</summary>
    public decimal AmountPerPoint { get; set; } = 10_000m;

    public int SilverThreshold { get; set; } = 1_000;

    public int GoldThreshold { get; set; } = 5_000;

    public LoyaltyTierThresholds Thresholds => new(SilverThreshold, GoldThreshold);
}

/// <summary>Identity -> Customer: create the customer profile for a newly registered Customer account. Idempotent.</summary>
public sealed class CustomerRegisteredHandler(CustomerEngagementDbContext db, ILogger<CustomerRegisteredHandler> logger)
    : IIntegrationEventHandler<CustomerRegisteredEvent>
{
    public async Task HandleAsync(CustomerRegisteredEvent @event, CancellationToken cancellationToken)
    {
        if (await db.Customers.AnyAsync(c => c.UserId == @event.UserId, cancellationToken))
        {
            return;
        }

        db.Customers.Add(new Customer(@event.UserId, @event.FullName, @event.Email, @event.Phone));
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Created customer profile for user {UserId}", @event.UserId);
    }
}

/// <summary>
/// Order -> Customer: BR-25 award points when an order reaches Completed (cancelled orders never publish this event).
/// The event's CustomerId may be either the customer profile id or the customer's user id. Idempotent per order.
/// </summary>
public sealed class OrderCompletedHandler(
    CustomerEngagementDbContext db, Microsoft.Extensions.Options.IOptions<LoyaltyOptions> options, ILogger<OrderCompletedHandler> logger)
    : IIntegrationEventHandler<OrderCompletedEvent>
{
    public async Task HandleAsync(OrderCompletedEvent @event, CancellationToken cancellationToken)
    {
        if (@event.CustomerId is null)
        {
            return; // walk-in guest
        }

        if (await db.LoyaltyTransactions.AnyAsync(t => t.OrderId == @event.OrderId, cancellationToken))
        {
            return; // already processed
        }

        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == @event.CustomerId || c.UserId == @event.CustomerId, cancellationToken);
        if (customer is null)
        {
            logger.LogWarning("OrderCompleted {OrderId}: customer {CustomerId} not found, no points awarded", @event.OrderId, @event.CustomerId);
            return;
        }

        var settings = options.Value;
        var points = (int)Math.Floor(@event.TotalAmount / settings.AmountPerPoint);
        if (points <= 0)
        {
            return;
        }

        customer.Earn(points, settings.Thresholds);
        db.LoyaltyTransactions.Add(new LoyaltyTransaction(customer.Id, @event.OrderId, points, LoyaltyTransactionType.Earn, $"Order total {@event.TotalAmount:0.##}"));
        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>Identity -> Customer: a customer edited their name or phone in Identity (UC-03); keep this copy in sync. Idempotent.</summary>
public sealed class UserProfileUpdatedHandler(CustomerEngagementDbContext db) : IIntegrationEventHandler<UserProfileUpdatedEvent>
{
    public async Task HandleAsync(UserProfileUpdatedEvent @event, CancellationToken cancellationToken)
    {
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.UserId == @event.UserId, cancellationToken);
        if (customer is null)
        {
            return; // the profile is created by CustomerRegisteredEvent; nothing to sync yet
        }

        customer.UpdateProfile(@event.FullName, @event.Phone, customer.Birthday, customer.MarketingOptIn);
        await db.SaveChangesAsync(cancellationToken);
    }
}
