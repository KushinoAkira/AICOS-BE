using AICOS.BuildingBlocks.Domain;

namespace AICOS.CustomerEngagement.Api.Domain;

/// <summary>
/// How a brand awards and redeems loyalty points (UC-16, FR-14). One row per tenant. A brand that never configured it uses the
/// platform defaults from appsettings (<c>Loyalty</c>), see <see cref="EffectiveLoyaltyRule"/>.
/// </summary>
public sealed class LoyaltyRule : AuditableEntity<Guid>
{
    private LoyaltyRule()
    {
    }

    public LoyaltyRule(Guid tenantId, EffectiveLoyaltyRule values)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        Apply(values);
    }

    public Guid TenantId { get; private set; }

    /// <summary>When false the brand awards no points (the programme is paused). Existing balances stay.</summary>
    public bool IsActive { get; private set; }

    /// <summary>BR-44: one point for each this much money spent on a completed, paid order (VND).</summary>
    public decimal AmountPerPoint { get; private set; }

    /// <summary>BR-73: how much discount one point is worth when points are redeemed (VND).</summary>
    public decimal RedeemValuePerPoint { get; private set; }

    /// <summary>The fewest points a customer can redeem at once.</summary>
    public int MinPointsToRedeem { get; private set; }

    public int SilverThreshold { get; private set; }

    public int GoldThreshold { get; private set; }

    public void Apply(EffectiveLoyaltyRule v)
    {
        IsActive = v.IsActive;
        AmountPerPoint = v.AmountPerPoint;
        RedeemValuePerPoint = v.RedeemValuePerPoint;
        MinPointsToRedeem = v.MinPointsToRedeem;
        SilverThreshold = v.SilverThreshold;
        GoldThreshold = v.GoldThreshold;
    }

    public EffectiveLoyaltyRule ToValues() =>
        new(IsActive, AmountPerPoint, RedeemValuePerPoint, MinPointsToRedeem, SilverThreshold, GoldThreshold, IsDefault: false);
}

/// <summary>The rules in force for a tenant: its own configuration, or the platform defaults when it has none.</summary>
public sealed record EffectiveLoyaltyRule(
    bool IsActive, decimal AmountPerPoint, decimal RedeemValuePerPoint, int MinPointsToRedeem, int SilverThreshold, int GoldThreshold, bool IsDefault)
{
    public LoyaltyTierThresholds Thresholds => new(SilverThreshold, GoldThreshold);
}
