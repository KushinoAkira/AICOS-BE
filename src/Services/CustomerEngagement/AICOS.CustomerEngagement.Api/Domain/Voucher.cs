using AICOS.BuildingBlocks.Domain;
using AICOS.BuildingBlocks.Exceptions;

namespace AICOS.CustomerEngagement.Api.Domain;

public enum VoucherType
{
    /// <summary>Discount voucher (percentage or fixed amount).</summary>
    Voucher,

    /// <summary>Prepaid gift card worth a fixed amount.</summary>
    GiftCard,
}

public enum VoucherDiscountType
{
    Percentage,
    FixedAmount,
}

public enum VoucherStatus
{
    Active,
    Redeemed,
    Revoked,
}

/// <summary>
/// Voucher / gift card issued by a brand (UC-17, FR-15). Single use. BR-35: an expired, redeemed or revoked code can
/// never be used again. <see cref="Version"/> prevents two cashiers redeeming the same code at the same moment.
/// </summary>
public sealed class Voucher : AuditableEntity<Guid>
{
    private Voucher()
    {
    }

    public Voucher(
        Guid tenantId, Guid? branchId, string code, VoucherType type, VoucherDiscountType discountType, decimal value, decimal? minOrderAmount,
        DateTime expiresAtUtc, string? issuedToName, Guid? issuedToCustomerId)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        BranchId = branchId;
        Code = code;
        Type = type;
        DiscountType = discountType;
        Value = value;
        MinOrderAmount = minOrderAmount;
        ExpiresAtUtc = expiresAtUtc;
        IssuedToName = string.IsNullOrWhiteSpace(issuedToName) ? null : issuedToName.Trim();
        IssuedToCustomerId = issuedToCustomerId;
        Status = VoucherStatus.Active;
    }

    public Guid TenantId { get; private set; }

    /// <summary>Null means the voucher is valid at every branch of the brand.</summary>
    public Guid? BranchId { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public VoucherType Type { get; private set; }

    public VoucherDiscountType DiscountType { get; private set; }

    public decimal Value { get; private set; }

    public decimal? MinOrderAmount { get; private set; }

    public DateTime ExpiresAtUtc { get; private set; }

    public string? IssuedToName { get; private set; }

    public Guid? IssuedToCustomerId { get; private set; }

    public VoucherStatus Status { get; private set; }

    public DateTime? RedeemedAtUtc { get; private set; }

    public string? RedeemedOrderReference { get; private set; }

    public decimal? RedeemedDiscount { get; private set; }

    public string? RevokeReason { get; private set; }

    public int Version { get; private set; }

    /// <summary>Set on a voucher customers hunt for: how many paid orders a member needs before claiming it. Null for an ordinary code.</summary>
    public int? RequiredOrders { get; private set; }

    /// <summary>A hunt voucher counts only orders of at least this amount (null: any amount).</summary>
    public decimal? RequiredOrderAmount { get; private set; }

    /// <summary>A hunt voucher counts orders paid from this moment.</summary>
    public DateTime? CountOrdersFromUtc { get; private set; }

    /// <summary>The hunt voucher this personal code was claimed from.</summary>
    public Guid? CampaignId { get; private set; }

    /// <summary>A hunt voucher is a template: members claim it in the app and each gets a personal single-use code.</summary>
    public bool IsHunt => RequiredOrders is not null;

    public bool IsExpired(DateTime nowUtc) => ExpiresAtUtc <= nowUtc;

    public void MakeHunt(int requiredOrders, decimal? requiredOrderAmount, DateTime countOrdersFromUtc)
    {
        RequiredOrders = requiredOrders;
        RequiredOrderAmount = requiredOrderAmount is > 0 ? requiredOrderAmount : null;
        CountOrdersFromUtc = countOrdersFromUtc;
    }

    /// <summary>The personal code a member gets when claiming this hunt voucher: same discount, conditions and expiry, usable once, only by them.</summary>
    public Voucher IssueClaim(string code, Guid customerId, string? customerName)
    {
        if (!IsHunt)
        {
            throw new BusinessRuleException("voucher.not_hunt", "Only a hunt voucher can be claimed.");
        }

        return new Voucher(TenantId, BranchId, code, Type, DiscountType, Value, MinOrderAmount, ExpiresAtUtc, customerName, customerId) { CampaignId = Id };
    }

    /// <summary>Discount for an order of <paramref name="orderAmount"/>, after checking BR-35 and the minimum order amount.</summary>
    public decimal CalculateDiscount(decimal orderAmount, DateTime nowUtc)
    {
        if (IsHunt)
        {
            throw new BusinessRuleException("voucher.hunt_only", "This voucher is claimed in the app first; each member then uses their own code.");
        }

        if (Status == VoucherStatus.Redeemed)
        {
            throw new BusinessRuleException("voucher.already_redeemed", "BR-35: this code has already been used.");
        }

        if (Status == VoucherStatus.Revoked)
        {
            throw new BusinessRuleException("voucher.revoked", "BR-35: this code has been revoked.");
        }

        if (IsExpired(nowUtc))
        {
            throw new BusinessRuleException("voucher.expired", "BR-35: this code has expired.");
        }

        if (MinOrderAmount is not null && orderAmount < MinOrderAmount)
        {
            throw new BusinessRuleException("voucher.min_order_not_met", $"This code needs an order of at least {MinOrderAmount:0.##}.");
        }

        var discount = DiscountType == VoucherDiscountType.Percentage ? Math.Round(orderAmount * Value / 100, 0) : Value;
        return Math.Min(discount, orderAmount);
    }

    public decimal Redeem(decimal orderAmount, string orderReference, DateTime nowUtc)
    {
        var discount = CalculateDiscount(orderAmount, nowUtc);
        Status = VoucherStatus.Redeemed;
        RedeemedAtUtc = nowUtc;
        RedeemedOrderReference = orderReference.Trim();
        RedeemedDiscount = discount;
        Version++;
        return discount;
    }

    /// <summary>
    /// Gives a redeemed code back because the order it was used on never happened (cancelled before payment). Only the order it was redeemed
    /// for can release it; an expired code is released too, and is then simply expired.
    /// </summary>
    public bool Release(string orderReference)
    {
        if (Status != VoucherStatus.Redeemed || !string.Equals(RedeemedOrderReference, orderReference.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        Status = VoucherStatus.Active;
        RedeemedAtUtc = null;
        RedeemedOrderReference = null;
        RedeemedDiscount = null;
        Version++;
        return true;
    }

    public void Revoke(string reason)
    {
        if (Status != VoucherStatus.Active)
        {
            throw new BusinessRuleException("voucher.invalid_status", $"A {Status} code cannot be revoked.");
        }

        Status = VoucherStatus.Revoked;
        RevokeReason = reason.Trim();
        Version++;
    }
}
