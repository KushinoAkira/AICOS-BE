using AICOS.BuildingBlocks.Validation;
using AICOS.IdentityOrganization.Api.Domain;

namespace AICOS.IdentityOrganization.Api.Features.Subscriptions;

/// <summary>POST/PUT /subscription-plans. Fee is VND per billing period. A null limit means unlimited.</summary>
public sealed record PlanRequest(
    string? Name, string? Tier, decimal? Fee, BillingPeriod? BillingPeriod, int? MaxBranches, int? MaxStaffAccounts, string? Description);

public sealed record PlanStatusRequest(bool? IsActive);

public sealed record PlanResponse(
    Guid Id,
    string Name,
    string Tier,
    decimal Fee,
    BillingPeriod BillingPeriod,
    int? MaxBranches,
    int? MaxStaffAccounts,
    string? Description,
    bool IsActive,
    int Subscribers,
    DateTime CreatedAtUtc,
    DateTime? UpdatedAtUtc)
{
    public static PlanResponse From(SubscriptionPlan p, int subscribers) => new(
        p.Id, p.Name, p.Tier, p.Fee, p.BillingPeriod, p.MaxBranches, p.MaxStaffAccounts, p.Description, p.IsActive, subscribers,
        p.CreatedAtUtc, p.UpdatedAtUtc);
}

/// <summary>POST /tenants/{id}/subscription: put the tenant on a plan. <see cref="StartDate"/> defaults to now.</summary>
public sealed record AssignPlanRequest(Guid? PlanId, DateTime? StartDate);

/// <summary>POST /tenants/{id}/subscription/payments: an amount received for the current period (billing is simulated).</summary>
public sealed record RecordPaymentRequest(decimal? Amount, string? Method, string? Reference, DateTime? PaidAt);

public sealed record SubscriptionResponse(
    Guid Id,
    Guid TenantId,
    string TenantName,
    Guid PlanId,
    string PlanName,
    decimal Fee,
    BillingPeriod BillingPeriod,
    SubscriptionStatus Status,
    DateTime PeriodStartUtc,
    DateTime PeriodEndUtc,
    SubscriptionPaymentStatus PaymentStatus,
    decimal PaidAmount,
    decimal Outstanding,
    DateTime? PaidAtUtc,
    SubscriptionHealth Health,
    int DaysUntilRenewal,
    int ActiveBranches,
    int StaffAccounts,
    int? MaxBranches,
    int? MaxStaffAccounts);

public sealed record SubscriptionPaymentResponse(
    Guid Id, Guid SubscriptionId, decimal Amount, DateTime PaidAtUtc, string Method, string? Reference, DateTime PeriodStartUtc, Guid? RecordedBy)
{
    public static SubscriptionPaymentResponse From(SubscriptionPayment p) =>
        new(p.Id, p.SubscriptionId, p.Amount, p.PaidAtUtc, p.Method, p.Reference, p.PeriodStartUtc, p.RecordedBy);
}

internal static class SubscriptionRequestValidator
{
    public const decimal MaxFee = 1_000_000_000m;

    public static void EnsureValid(PlanRequest r)
    {
        var errors = new ValidationErrors();
        CommonRules.Required(errors, nameof(r.Name), r.Name, "Plan name", SubscriptionPlan.NameMaxLength);
        CommonRules.Required(errors, nameof(r.Tier), r.Tier, "Tier", SubscriptionPlan.TierMaxLength);

        if (r.Fee is null or < 0 or > MaxFee)
        {
            errors.Add(nameof(r.Fee), $"Fee must be between 0 and {MaxFee:N0} VND.");
        }

        if (r.BillingPeriod is null || !Enum.IsDefined(r.BillingPeriod.Value))
        {
            errors.Add(nameof(r.BillingPeriod), "Billing period must be Monthly, Quarterly or Yearly.");
        }

        if (r.MaxBranches is < 1 or > 1000)
        {
            errors.Add(nameof(r.MaxBranches), "Branch limit must be between 1 and 1000, or empty for unlimited.");
        }

        if (r.MaxStaffAccounts is < 1 or > 10000)
        {
            errors.Add(nameof(r.MaxStaffAccounts), "Staff account limit must be between 1 and 10000, or empty for unlimited.");
        }

        CommonRules.MaxLength(errors, nameof(r.Description), r.Description, "Description", SubscriptionPlan.DescriptionMaxLength);
        errors.ThrowIfAny();
    }

    public static void EnsureValid(RecordPaymentRequest r)
    {
        var errors = new ValidationErrors();
        if (r.Amount is null or <= 0 or > MaxFee)
        {
            errors.Add(nameof(r.Amount), $"Amount must be greater than 0 and at most {MaxFee:N0} VND.");
        }

        CommonRules.Required(errors, nameof(r.Method), r.Method, "Payment method", SubscriptionPayment.MethodMaxLength);
        CommonRules.MaxLength(errors, nameof(r.Reference), r.Reference, "Reference", SubscriptionPayment.ReferenceMaxLength);
        errors.ThrowIfAny();
    }
}

/// <summary>One row of GET /subscriptions: the platform owner's view of every tenant's billing position.</summary>
public sealed record SubscriptionSummaryResponse(
    Guid Id,
    Guid TenantId,
    string TenantName,
    string TenantCode,
    string PlanName,
    decimal Fee,
    BillingPeriod BillingPeriod,
    DateTime PeriodEndUtc,
    SubscriptionPaymentStatus PaymentStatus,
    decimal Outstanding,
    SubscriptionHealth Health,
    int DaysUntilRenewal);

/// <summary>What the landing page shows for a plan that can be bought: no subscriber counts or audit fields.</summary>
public sealed record PublicPlanResponse(
    Guid Id,
    string Name,
    string Tier,
    decimal Fee,
    BillingPeriod BillingPeriod,
    int? MaxBranches,
    int? MaxStaffAccounts,
    string? Description);
