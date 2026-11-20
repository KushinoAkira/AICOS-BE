using AICOS.BuildingBlocks.Messaging;
using AICOS.Contracts.Events;
using AICOS.CustomerEngagement.Api.Domain;
using AICOS.CustomerEngagement.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.CustomerEngagement.Api.Features.Campaigns;

/// <summary>Creates a brand-wide combo voucher when Apriori promotes a strong buy-together rule (idempotent by PairKey → code).</summary>
public sealed class AssociationRulePromoHandler(CustomerEngagementDbContext db, TimeProvider clock, ILogger<AssociationRulePromoHandler> logger)
    : IIntegrationEventHandler<AssociationRulePromotedEvent>
{
    private const int DiscountPercent = 10;
    private const int ValidDays = 30;

    public async Task HandleAsync(AssociationRulePromotedEvent @event, CancellationToken cancellationToken)
    {
        var code = "COMBO-" + @event.PairKey[..Math.Min(8, @event.PairKey.Length)].ToUpperInvariant();
        if (await db.Vouchers.AnyAsync(v => v.TenantId == @event.TenantId && v.Code == code, cancellationToken))
        {
            return;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var label = $"{@event.AntecedentNames} + {@event.ConsequentNames}";
        var voucher = new Voucher(
            @event.TenantId,
            @event.BranchId,
            code,
            VoucherType.Voucher,
            VoucherDiscountType.Percentage,
            DiscountPercent,
            minOrderAmount: 50_000,
            expiresAtUtc: now.AddDays(ValidDays),
            issuedToName: $"Apriori: {label}".Length > 255 ? $"Apriori combo {code}" : $"Apriori: {label}",
            issuedToCustomerId: null);
        db.Vouchers.Add(voucher);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Combo voucher {Code} created for tenant {TenantId} ({Antecedent} => {Consequent}, lift={Lift:F2})",
            code, @event.TenantId, @event.AntecedentNames, @event.ConsequentNames, @event.Lift);
    }
}
