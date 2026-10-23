using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Security;
using AICOS.BuildingBlocks.Validation;
using AICOS.CustomerEngagement.Api.Domain;
using AICOS.CustomerEngagement.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AICOS.CustomerEngagement.Api.Features.Loyalty;

/// <summary>PUT /loyalty/rules: the whole rule set of the brand. Amounts are VND.</summary>
public sealed record LoyaltyRuleRequest(
    bool? IsActive, decimal? AmountPerPoint, decimal? RedeemValuePerPoint, int? MinPointsToRedeem, int? SilverThreshold, int? GoldThreshold);

/// <summary><see cref="IsDefault"/> is true while the brand has not configured its own rules and the platform defaults apply.</summary>
public sealed record LoyaltyRuleResponse(
    bool IsActive, decimal AmountPerPoint, decimal RedeemValuePerPoint, int MinPointsToRedeem, int SilverThreshold, int GoldThreshold, bool IsDefault)
{
    public static LoyaltyRuleResponse From(EffectiveLoyaltyRule r) =>
        new(r.IsActive, r.AmountPerPoint, r.RedeemValuePerPoint, r.MinPointsToRedeem, r.SilverThreshold, r.GoldThreshold, r.IsDefault);
}

/// <summary>Finds the loyalty rules in force for a tenant: its own, or the platform defaults (BR-47: points belong to the brand).</summary>
public sealed class LoyaltyRulesService(CustomerEngagementDbContext db, IOptions<LoyaltyOptions> defaults)
{
    public async Task<EffectiveLoyaltyRule> ForTenantAsync(Guid tenantId, CancellationToken ct)
    {
        var own = await db.LoyaltyRules.AsNoTracking().FirstOrDefaultAsync(r => r.TenantId == tenantId, ct);
        return own?.ToValues() ?? Defaults();
    }

    public EffectiveLoyaltyRule Defaults()
    {
        var d = defaults.Value;
        return new EffectiveLoyaltyRule(true, d.AmountPerPoint, d.RedeemValuePerPoint, d.MinPointsToRedeem, d.SilverThreshold, d.GoldThreshold, IsDefault: true);
    }
}

/// <summary>
/// UC-16 Configure Loyalty Rules. Routes: /api/v1/customers/loyalty/rules. Read: BrandAdmin and BranchManager. Write: BrandAdmin.
/// Changing the tier thresholds re-evaluates the tier of every customer of the brand; points already earned are never rewritten.
/// </summary>
public static class LoyaltyRuleEndpoints
{
    public static void MapLoyaltyRuleEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/loyalty/rules").WithTags("Loyalty rules");

        group.MapGet("/", GetAsync).RequireAuthorization(AicosPolicies.Management);
        group.MapPut("/", UpdateAsync).RequireAuthorization(AicosPolicies.AdminOnly);
    }

    private static async Task<LoyaltyRuleResponse> GetAsync(LoyaltyRulesService rules, ICurrentUser user, CancellationToken ct) =>
        LoyaltyRuleResponse.From(await rules.ForTenantAsync(TenantScope.Require(user), ct));

    private static async Task<LoyaltyRuleResponse> UpdateAsync(
        LoyaltyRuleRequest r,
        CustomerEngagementDbContext db,
        LoyaltyRulesService rules,
        ICurrentUser user,
        IEventBus bus,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        Validate(r);
        var tenantId = TenantScope.Require(user);
        var before = await rules.ForTenantAsync(tenantId, ct);
        var values = new EffectiveLoyaltyRule(
            r.IsActive!.Value, r.AmountPerPoint!.Value, r.RedeemValuePerPoint!.Value, r.MinPointsToRedeem!.Value,
            r.SilverThreshold!.Value, r.GoldThreshold!.Value, IsDefault: false);

        var rule = await db.LoyaltyRules.FirstOrDefaultAsync(x => x.TenantId == tenantId, ct);
        if (rule is null)
        {
            db.LoyaltyRules.Add(new LoyaltyRule(tenantId, values));
        }
        else
        {
            rule.Apply(values);
        }

        // Tiers follow the lifetime points, so new thresholds can move customers up or down.
        foreach (var customer in await db.Customers.Where(c => c.TenantId == tenantId).ToListAsync(ct))
        {
            customer.RecalculateTier(values.Thresholds);
        }

        await db.SaveChangesAsync(ct);
        await AuditTrail.PublishAsync(
            bus, user, loggers.CreateLogger("AICOS.Customer.Loyalty"), tenantId, "loyalty_rule.updated", "LoyaltyRule", tenantId, Snapshot(before), Snapshot(values));

        return LoyaltyRuleResponse.From(values);
    }

    private static object Snapshot(EffectiveLoyaltyRule r) => new
    {
        r.IsActive, r.AmountPerPoint, r.RedeemValuePerPoint, r.MinPointsToRedeem, r.SilverThreshold, r.GoldThreshold, r.IsDefault,
    };

    private static void Validate(LoyaltyRuleRequest r)
    {
        var errors = new ValidationErrors();
        if (r.IsActive is null)
        {
            errors.Add("IsActive", "IsActive is required (true or false).");
        }

        if (r.AmountPerPoint is null or <= 0 or > 100_000_000m)
        {
            errors.Add("AmountPerPoint", "Amount per point must be greater than 0 and at most 100,000,000 VND.");
        }

        if (r.RedeemValuePerPoint is null or <= 0 or > 1_000_000m)
        {
            errors.Add("RedeemValuePerPoint", "Redeem value per point must be greater than 0 and at most 1,000,000 VND.");
        }

        if (r.MinPointsToRedeem is null or < 1 or > 1_000_000)
        {
            errors.Add("MinPointsToRedeem", "Minimum points to redeem must be between 1 and 1,000,000.");
        }

        if (r.SilverThreshold is null or < 1 or > 10_000_000)
        {
            errors.Add("SilverThreshold", "Silver threshold must be between 1 and 10,000,000 points.");
        }

        if (r.GoldThreshold is null or < 1 or > 10_000_000)
        {
            errors.Add("GoldThreshold", "Gold threshold must be between 1 and 10,000,000 points.");
        }
        else if (r.SilverThreshold is not null && r.GoldThreshold <= r.SilverThreshold)
        {
            errors.Add("GoldThreshold", "Gold threshold must be higher than the silver threshold.");
        }

        errors.ThrowIfAny();
    }
}
