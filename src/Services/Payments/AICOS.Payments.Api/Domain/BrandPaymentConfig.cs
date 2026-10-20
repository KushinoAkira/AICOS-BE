using AICOS.BuildingBlocks.Domain;

namespace AICOS.Payments.Api.Domain;

/// <summary>Who approves a refund that falls outside the brand policy (BR-30).</summary>
public enum RefundApprover
{
    BranchManager,
    BrandAdmin,
}

/// <summary>
/// The brand rules for cancelling and refunding (UC-18, FR-16, BR-29, BR-30, BR-75). One row per tenant; a brand that never
/// configured it uses <see cref="EffectiveCancellationPolicy.Defaults"/>. A refund that follows these rules is approved
/// automatically (when <see cref="AutoApproveWithinPolicy"/> is on); anything else is an exception that needs the approver.
/// The lock-time before pickup after which a customer cannot cancel is the pickup slot cut-off (BR-23, BR-24), configured in the
/// Ordering service.
/// </summary>
public sealed class CancellationPolicy : AuditableEntity<Guid>
{
    private CancellationPolicy()
    {
    }

    public CancellationPolicy(Guid tenantId, EffectiveCancellationPolicy values)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        Apply(values);
    }

    public Guid TenantId { get; private set; }

    /// <summary>BR-30 / BR-75: percentage refunded automatically when a customer cancels before preparation starts (0-100).</summary>
    public int CancelRefundPercent { get; private set; }

    /// <summary>BR-58: minutes after the pickup time before a prepaid takeaway order that nobody collected becomes a no-show.</summary>
    public int NoShowAfterMinutes { get; private set; }

    /// <summary>Percentage refunded for a no-show order (0-100, usually 0).</summary>
    public int NoShowRefundPercent { get; private set; }

    /// <summary>BR-25 / BR-60: minutes an unpaid online order and its held slot are kept before they are released.</summary>
    public int UnpaidOrderTimeoutMinutes { get; private set; }

    /// <summary>When true, a refund within the policy is approved without a person (BR-75). When false every refund needs the approver.</summary>
    public bool AutoApproveWithinPolicy { get; private set; }

    public RefundApprover ExceptionApprover { get; private set; }

    public void Apply(EffectiveCancellationPolicy v)
    {
        CancelRefundPercent = v.CancelRefundPercent;
        NoShowAfterMinutes = v.NoShowAfterMinutes;
        NoShowRefundPercent = v.NoShowRefundPercent;
        UnpaidOrderTimeoutMinutes = v.UnpaidOrderTimeoutMinutes;
        AutoApproveWithinPolicy = v.AutoApproveWithinPolicy;
        ExceptionApprover = v.ExceptionApprover;
    }

    public EffectiveCancellationPolicy ToValues() => new(
        CancelRefundPercent, NoShowAfterMinutes, NoShowRefundPercent, UnpaidOrderTimeoutMinutes, AutoApproveWithinPolicy, ExceptionApprover, IsDefault: false);
}

/// <summary>The policy in force for a tenant: its own, or the platform defaults when it has none.</summary>
public sealed record EffectiveCancellationPolicy(
    int CancelRefundPercent, int NoShowAfterMinutes, int NoShowRefundPercent, int UnpaidOrderTimeoutMinutes, bool AutoApproveWithinPolicy,
    RefundApprover ExceptionApprover, bool IsDefault)
{
    /// <summary>Full refund when cancelled in time, no refund for a no-show after 30 minutes, unpaid orders released after 15 minutes.</summary>
    public static EffectiveCancellationPolicy Defaults { get; } = new(100, 30, 0, 15, true, RefundApprover.BranchManager, IsDefault: true);
}

/// <summary>
/// How a brand (or one of its branches) gets paid through a provider (UC-20, FR-18): which provider is switched on and the receiving
/// account. A branch setting replaces the brand-wide one for that branch. The account number is encrypted at rest and never returned in full.
/// </summary>
public sealed class MerchantSetting : AuditableEntity<Guid>
{
    public const int AccountNameMaxLength = 100;
    public const int BankCodeMaxLength = 20;
    public const int MerchantCodeMaxLength = 50;

    private MerchantSetting()
    {
    }

    public MerchantSetting(Guid tenantId, Guid? branchId, string providerCode)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        BranchId = branchId;
        ProviderCode = providerCode;
    }

    public Guid TenantId { get; private set; }

    /// <summary>Null means brand-wide.</summary>
    public Guid? BranchId { get; private set; }

    /// <summary>One of the provider codes configured by the platform (Mock, VietQR, Momo, ZaloPay).</summary>
    public string ProviderCode { get; private set; } = string.Empty;

    public bool IsEnabled { get; private set; }

    public string? AccountName { get; private set; }

    public string? BankCode { get; private set; }

    public string? AccountNumberEncrypted { get; private set; }

    /// <summary>Last 4 characters of the account number, kept to tell accounts apart without decrypting.</summary>
    public string? AccountNumberHint { get; private set; }

    /// <summary>Merchant / partner code for wallets such as Momo and ZaloPay.</summary>
    public string? MerchantCode { get; private set; }

    public void Update(bool isEnabled, string? accountName, string? bankCode, string? merchantCode)
    {
        IsEnabled = isEnabled;
        AccountName = Clean(accountName);
        BankCode = Clean(bankCode)?.ToUpperInvariant();
        MerchantCode = Clean(merchantCode);
    }

    /// <summary>Stores a new account number (already encrypted). Null keeps the current one.</summary>
    public void SetAccountNumber(string? encrypted, string? hint)
    {
        if (encrypted is null)
        {
            return;
        }

        AccountNumberEncrypted = encrypted;
        AccountNumberHint = hint;
    }

    public bool HasPaymentDetails => AccountNumberEncrypted is not null || !string.IsNullOrEmpty(MerchantCode);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
