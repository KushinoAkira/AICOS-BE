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

    /// <summary>BR-73 default: 100 VND of discount per point redeemed.</summary>
    public decimal RedeemValuePerPoint { get; set; } = 100m;

    public int MinPointsToRedeem { get; set; } = 50;

    /// <summary>How long a voucher bought with points stays valid.</summary>
    public int RedeemedVoucherDays { get; set; } = 90;

    public LoyaltyTierThresholds Thresholds => new(SilverThreshold, GoldThreshold);
}

/// <summary>Identity -> Customer: create the customer's profile at a brand the first time they choose it. Idempotent per account and brand.</summary>
public sealed class CustomerRegisteredHandler(CustomerEngagementDbContext db, ILogger<CustomerRegisteredHandler> logger)
    : IIntegrationEventHandler<CustomerRegisteredEvent>
{
    public async Task HandleAsync(CustomerRegisteredEvent @event, CancellationToken cancellationToken)
    {
        if (await db.Customers.AnyAsync(c => c.UserId == @event.UserId && c.TenantId == @event.TenantId, cancellationToken))
        {
            return;
        }

        db.Customers.Add(new Customer(@event.UserId, @event.TenantId, @event.FullName, @event.Email, @event.Phone));
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Created customer profile for user {UserId}", @event.UserId);
    }
}

/// <summary>
/// Order -> Customer: BR-25 award points once an order is paid (SCRUM-133). A walk-in without a member gets nothing. The customer is named by
/// profile id or account id. Idempotent per order: the database allows one Earn per order, so a redelivered event adds nothing.
/// </summary>
public sealed class OrderPaidHandler(CustomerEngagementDbContext db, LoyaltyRulesService rules, ILogger<OrderPaidHandler> logger)
    : IIntegrationEventHandler<OrderPaidIntegrationEvent>
{
    public async Task HandleAsync(OrderPaidIntegrationEvent @event, CancellationToken cancellationToken)
    {
        if (@event.CustomerId is null)
        {
            return;
        }

        if (await db.LoyaltyTransactions.AnyAsync(t => t.OrderId == @event.OrderId && t.Type == LoyaltyTransactionType.Earn, cancellationToken))
        {
            return;
        }

        var customer = await LoyaltyLookup.CustomerAsync(db, @event.TenantId, @event.CustomerId.Value, cancellationToken);
        if (customer is null)
        {
            logger.LogWarning("OrderPaid {OrderId}: customer {CustomerId} not found in the brand, no points awarded", @event.OrderId, @event.CustomerId);
            return;
        }

        var rule = await rules.ForTenantAsync(customer.TenantId, cancellationToken); // the brand rules (BR-44, BR-47)
        var points = rule.IsActive ? (int)Math.Floor(@event.Amount / rule.AmountPerPoint) : 0;
        if (points <= 0)
        {
            return;
        }

        customer.Earn(points, rule.Thresholds);
        db.LoyaltyTransactions.Add(new LoyaltyTransaction(customer.Id, @event.OrderId, points, LoyaltyTransactionType.Earn, $"Order {@event.OrderCode}: paid {@event.Amount:0.##}"));
        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// Order -> Customer: an order was cancelled (SCRUM-133). Unpaid: the voucher used on it is given back, so the member does not lose it.
/// Paid (a manager exception, or the shop could not make it): the points earned on it are taken back, once. A no-show keeps the points: the customer paid and the shop earned. All safe to repeat.
/// </summary>
public sealed class OrderCancelledLoyaltyHandler(CustomerEngagementDbContext db, LoyaltyRulesService rules, ILogger<OrderCancelledLoyaltyHandler> logger)
    : IIntegrationEventHandler<OrderCancelledIntegrationEvent>
{
    public async Task HandleAsync(OrderCancelledIntegrationEvent @event, CancellationToken cancellationToken)
    {
        if (!@event.WasPaid)
        {
            await ReleaseVoucherAsync(@event, cancellationToken);
            return;
        }

        if (@event.CustomerId is null || @event.ClosedAs == "NoShow")   // a no-show: the shop kept the money (fully or in part), so the member keeps the points
        {
            return;
        }

        var earned = await db.LoyaltyTransactions.FirstOrDefaultAsync(t => t.OrderId == @event.OrderId && t.Type == LoyaltyTransactionType.Earn, cancellationToken);
        if (earned is null || await db.LoyaltyTransactions.AnyAsync(t => t.OrderId == @event.OrderId && t.Type == LoyaltyTransactionType.Reverse, cancellationToken))
        {
            return;
        }

        var customer = await LoyaltyLookup.CustomerAsync(db, @event.TenantId, @event.CustomerId.Value, cancellationToken);
        if (customer is null || customer.Id != earned.CustomerId)
        {
            logger.LogWarning("OrderCancelled {OrderId}: the member who earned the points was not found, nothing reversed", @event.OrderId);
            return;
        }

        var rule = await rules.ForTenantAsync(customer.TenantId, cancellationToken);
        var taken = customer.Reverse(earned.Points, rule.Thresholds);
        db.LoyaltyTransactions.Add(new LoyaltyTransaction(customer.Id, @event.OrderId, -taken, LoyaltyTransactionType.Reverse, $"Order {@event.OrderCode} cancelled after payment"));
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task ReleaseVoucherAsync(OrderCancelledIntegrationEvent @event, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(@event.PromotionCode) || string.IsNullOrWhiteSpace(@event.OrderCode))
        {
            return;
        }

        var code = @event.PromotionCode.Trim().ToUpperInvariant();
        var voucher = await db.Vouchers.FirstOrDefaultAsync(v => v.TenantId == @event.TenantId && v.Code == code, ct);
        if (voucher is not null && voucher.Release(@event.OrderCode))
        {
            await db.SaveChangesAsync(ct);
        }
    }
}

internal static class LoyaltyLookup
{
    /// <summary>The member of a brand, named by profile id or account id.</summary>
    public static Task<Customer?> CustomerAsync(CustomerEngagementDbContext db, Guid tenantId, Guid id, CancellationToken ct) =>
        db.Customers.FirstOrDefaultAsync(c => c.TenantId == tenantId && (c.Id == id || c.UserId == id), ct);
}

/// <summary>Identity -> Customer: a customer edited their name or phone in Identity (UC-03); keep this copy in sync. Idempotent.</summary>
public sealed class UserProfileUpdatedHandler(CustomerEngagementDbContext db) : IIntegrationEventHandler<UserProfileUpdatedEvent>
{
    public async Task HandleAsync(UserProfileUpdatedEvent @event, CancellationToken cancellationToken)
    {
        // One account has a profile at every brand it has visited.
        var profiles = await db.Customers.Where(c => c.UserId == @event.UserId).ToListAsync(cancellationToken);
        foreach (var customer in profiles)
        {
            customer.UpdateProfile(@event.FullName, @event.Phone, customer.Birthday, customer.MarketingOptIn);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
