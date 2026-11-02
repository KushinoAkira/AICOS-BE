using AICOS.BuildingBlocks.Domain;
using AICOS.BuildingBlocks.Exceptions;

namespace AICOS.CustomerEngagement.Api.Domain;

public enum LoyaltyTier
{
    Member,
    Silver,
    Gold,
}

/// <summary>
/// Customer profile + loyalty wallet (UC-30, UC-44). Created when Identity publishes CustomerRegisteredEvent;
/// <see cref="UserId"/> is the login account in Identity (no FK across services).
/// </summary>
public sealed class Customer : AuditableEntity<Guid>
{
    private Customer()
    {
    }

    public Customer(Guid userId, Guid tenantId, string fullName, string email, string? phone)
    {
        Id = Guid.NewGuid();
        UserId = userId;
        TenantId = tenantId;
        Email = email.Trim().ToLowerInvariant();
        Tier = LoyaltyTier.Member;
        UpdateProfile(fullName, phone, birthday: null, marketingOptIn: false);
    }

    public Guid UserId { get; private set; }

    /// <summary>The brand this customer belongs to; loyalty points belong to it too (BR-47, BR-72).</summary>
    public Guid TenantId { get; private set; }

    public string FullName { get; private set; } = string.Empty;

    public string Email { get; private set; } = string.Empty;

    public string? Phone { get; private set; }

    public DateOnly? Birthday { get; private set; }

    /// <summary>BR-34: campaigns are only sent to customers who opted in.</summary>
    public bool MarketingOptIn { get; private set; }

    /// <summary>Declared drink preferences (UC-03), used by personalised recommendations when there is no purchase history (BR-38).</summary>
    public int? PreferredSugarPercent { get; private set; }

    public string? PreferredIce { get; private set; }

    public List<string> FavoriteToppings { get; private set; } = [];

    public string? PreferenceNotes { get; private set; }

    public int PointsBalance { get; private set; }

    /// <summary>Bumped by every change of the points, so two changes at the same time cannot both succeed (no double spending).</summary>
    public int Version { get; private set; }

    /// <summary>All points ever earned (never decreases) - decides the tier.</summary>
    public int LifetimePoints { get; private set; }

    public LoyaltyTier Tier { get; private set; }

    public void UpdateProfile(string fullName, string? phone, DateOnly? birthday, bool marketingOptIn)
    {
        FullName = fullName.Trim();
        Phone = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim();
        Birthday = birthday;
        MarketingOptIn = marketingOptIn;
    }

    /// <summary>BR-70: removes what identifies the person; points and transactions stay for the brand's records.</summary>
    public void Anonymize()
    {
        Email = $"deleted-{UserId:N}@anonymized.invalid";
        UpdateProfile("Deleted customer", phone: null, birthday: null, marketingOptIn: false);
        PreferredSugarPercent = null;
        PreferredIce = null;
        PreferenceNotes = null;
    }

    /// <summary>Applies new tier thresholds to the points this customer has already earned.</summary>
    public void RecalculateTier(LoyaltyTierThresholds thresholds) => Tier = thresholds.TierFor(LifetimePoints);

    public void SetPreferences(int? sugarPercent, string? ice, IEnumerable<string> favoriteToppings, string? notes)
    {
        PreferredSugarPercent = sugarPercent;
        PreferredIce = string.IsNullOrWhiteSpace(ice) ? null : ice.Trim();
        FavoriteToppings = favoriteToppings.Select(t => t.Trim()).ToList();
        PreferenceNotes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
    }

    public void Earn(int points, LoyaltyTierThresholds thresholds)
    {
        PointsBalance += points;
        Version++;
        LifetimePoints += points;
        Tier = thresholds.TierFor(LifetimePoints);
    }


    /// <summary>Points spent on a voucher. Only the balance goes down: the lifetime points, which decide the tier, stay.</summary>
    public void Redeem(int points)
    {
        if (points > PointsBalance)
        {
            throw new BusinessRuleException("loyalty.insufficient_points", $"The customer only has {PointsBalance} points.");
        }

        PointsBalance -= points;
        Version++;
    }

    /// <summary>Takes back points earned on an order that was then cancelled after payment. Never below zero; returns the points really taken.</summary>
    public int Reverse(int points, LoyaltyTierThresholds thresholds)
    {
        var taken = Math.Min(points, PointsBalance);
        PointsBalance -= taken;
        Version++;
        LifetimePoints = Math.Max(0, LifetimePoints - points);
        Tier = thresholds.TierFor(LifetimePoints);
        return taken;
    }
    /// <summary>Manual correction by a manager. Positive adjustments also count towards the tier.</summary>
    public void Adjust(int points, LoyaltyTierThresholds thresholds)
    {
        if (PointsBalance + points < 0)
        {
            throw new BusinessRuleException("loyalty.insufficient_points", $"The customer only has {PointsBalance} points.");
        }

        PointsBalance += points;
        Version++;
        if (points > 0)
        {
            LifetimePoints += points;
            Tier = thresholds.TierFor(LifetimePoints);
        }
    }
}

public sealed record LoyaltyTierThresholds(int Silver, int Gold)
{
    public LoyaltyTier TierFor(int lifetimePoints) =>
        lifetimePoints >= Gold ? LoyaltyTier.Gold : lifetimePoints >= Silver ? LoyaltyTier.Silver : LoyaltyTier.Member;

    public int? NextThreshold(LoyaltyTier tier) => tier switch
    {
        LoyaltyTier.Member => Silver,
        LoyaltyTier.Silver => Gold,
        _ => null,
    };
}

public enum LoyaltyTransactionType
{
    /// <summary>Points from a completed order (BR-25).</summary>
    Earn,

    /// <summary>Manual correction by a manager.</summary>
    Adjust,

    /// <summary>Points spent on a voucher.</summary>
    Redeem,

    /// <summary>Points taken back because the paid order was cancelled.</summary>
    Reverse,
}

public sealed class LoyaltyTransaction : AuditableEntity<Guid>
{
    private LoyaltyTransaction()
    {
    }

    public LoyaltyTransaction(Guid customerId, Guid? orderId, int points, LoyaltyTransactionType type, string? note)
    {
        Id = Guid.NewGuid();
        CustomerId = customerId;
        OrderId = orderId;
        Points = points;
        Type = type;
        Note = note;
    }

    public Guid CustomerId { get; private set; }

    /// <summary>Order in Order Service; unique for Earn so a redelivered event never awards points twice.</summary>
    public Guid? OrderId { get; private set; }

    public int Points { get; private set; }

    public LoyaltyTransactionType Type { get; private set; }

    public string? Note { get; private set; }
}
