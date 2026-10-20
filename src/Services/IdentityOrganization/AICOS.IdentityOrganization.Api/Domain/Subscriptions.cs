using AICOS.BuildingBlocks.Domain;
using AICOS.BuildingBlocks.Exceptions;

namespace AICOS.IdentityOrganization.Api.Domain;

/// <summary>Length of one billing cycle; the value is the number of months.</summary>
public enum BillingPeriod
{
    Monthly = 1,
    Quarterly = 3,
    Yearly = 12,
}

/// <summary>
/// A subscription plan the platform sells to brands (UC-06, FR-03): tier, fee, billing period and limits (BR-11).
/// A plan is never deleted: deactivating it only stops new assignments, existing subscribers keep it.
/// </summary>
public sealed class SubscriptionPlan : AuditableEntity<Guid>
{
    public const int NameMaxLength = 100;
    public const int TierMaxLength = 30;
    public const int DescriptionMaxLength = 500;

    private SubscriptionPlan()
    {
    }

    public SubscriptionPlan(
        string name, string tier, decimal fee, BillingPeriod billingPeriod, int? maxBranches, int? maxStaffAccounts, string? description)
    {
        Id = Guid.NewGuid();
        IsActive = true;
        Update(name, tier, fee, billingPeriod, maxBranches, maxStaffAccounts, description);
    }

    public string Name { get; private set; } = string.Empty;

    /// <summary>Marketing tier label, e.g. Basic, Standard, Premium.</summary>
    public string Tier { get; private set; } = string.Empty;

    /// <summary>Fee per billing period, in VND.</summary>
    public decimal Fee { get; private set; }

    public BillingPeriod BillingPeriod { get; private set; }

    /// <summary>Maximum number of active branches. Null means unlimited.</summary>
    public int? MaxBranches { get; private set; }

    /// <summary>Maximum number of staff accounts (BrandAdmin, BranchManager, Barista, Cashier). Null means unlimited.</summary>
    public int? MaxStaffAccounts { get; private set; }

    public string? Description { get; private set; }

    public bool IsActive { get; private set; }

    public void Update(
        string name, string tier, decimal fee, BillingPeriod billingPeriod, int? maxBranches, int? maxStaffAccounts, string? description)
    {
        Name = name.Trim();
        Tier = tier.Trim();
        Fee = decimal.Round(fee, 0);
        BillingPeriod = billingPeriod;
        MaxBranches = maxBranches;
        MaxStaffAccounts = maxStaffAccounts;
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
    }

    public void SetActive(bool isActive) => IsActive = isActive;
}

public enum SubscriptionStatus
{
    /// <summary>The tenant's current subscription. A tenant has at most one.</summary>
    Active,

    /// <summary>Replaced by another plan (upgrade or downgrade); kept for history.</summary>
    Superseded,
}

public enum SubscriptionPaymentStatus
{
    /// <summary>The fee of the current period has not been fully paid.</summary>
    Unpaid,

    Paid,
}

/// <summary>Where a subscription stands against its renewal date (BR-10, BR-67).</summary>
public enum SubscriptionHealth
{
    /// <summary>The renewal date is still far away.</summary>
    Current,

    /// <summary>The renewal date is close: the brand should be reminded.</summary>
    DueSoon,

    /// <summary>The period ended without renewal; the brand is inside the grace period.</summary>
    GracePeriod,

    /// <summary>The grace period is over: the Super Admin may suspend the tenant.</summary>
    Overdue,
}

/// <summary>
/// The plan a tenant is on, with the dates and payment status of its current billing period (UC-07, FR-04).
/// Billing is simulated (MVP): the Super Admin records payments by hand. Plan name, fee and period are copied at
/// assignment so a later change of the plan never rewrites what a tenant agreed to.
/// </summary>
public sealed class TenantSubscription : AuditableEntity<Guid>
{
    private TenantSubscription()
    {
    }

    public TenantSubscription(Guid tenantId, SubscriptionPlan plan, DateTime periodStartUtc)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        PlanId = plan.Id;
        PlanName = plan.Name;
        Fee = plan.Fee;
        BillingPeriod = plan.BillingPeriod;
        Status = SubscriptionStatus.Active;
        StartPeriod(periodStartUtc);
    }

    public Guid TenantId { get; private set; }

    public Guid PlanId { get; private set; }

    public string PlanName { get; private set; } = string.Empty;

    public decimal Fee { get; private set; }

    public BillingPeriod BillingPeriod { get; private set; }

    public SubscriptionStatus Status { get; private set; }

    public DateTime PeriodStartUtc { get; private set; }

    /// <summary>The renewal date: the end of the current period.</summary>
    public DateTime PeriodEndUtc { get; private set; }

    public SubscriptionPaymentStatus PaymentStatus { get; private set; }

    /// <summary>Amount received so far for the current period.</summary>
    public decimal PaidAmount { get; private set; }

    public DateTime? PaidAtUtc { get; private set; }

    /// <summary>The period end a renewal reminder was already published for; stops the job from repeating it.</summary>
    public DateTime? RenewalNoticeForPeriodEndUtc { get; private set; }

    public DateTime? OverdueNoticeForPeriodEndUtc { get; private set; }

    public decimal Outstanding => Math.Max(0, Fee - PaidAmount);

    public SubscriptionHealth HealthAt(DateTime nowUtc, int renewalNoticeDays, int gracePeriodDays)
    {
        if (nowUtc > PeriodEndUtc.AddDays(gracePeriodDays))
        {
            return SubscriptionHealth.Overdue;
        }

        if (nowUtc > PeriodEndUtc)
        {
            return SubscriptionHealth.GracePeriod;
        }

        return nowUtc >= PeriodEndUtc.AddDays(-renewalNoticeDays) ? SubscriptionHealth.DueSoon : SubscriptionHealth.Current;
    }

    /// <summary>Adds a payment for the current period. Overpaying is refused: the Super Admin records exactly what was received.</summary>
    public void AddPayment(decimal amount, DateTime paidAtUtc)
    {
        if (amount > Outstanding)
        {
            throw new BusinessRuleException(
                "subscription.overpayment", $"Only {Outstanding:N0} VND is outstanding for the current period.");
        }

        PaidAmount += amount;
        if (Outstanding == 0)
        {
            PaymentStatus = SubscriptionPaymentStatus.Paid;
            PaidAtUtc = paidAtUtc;
        }
    }

    /// <summary>
    /// Starts the next billing period (BR-10). The current period must be paid. A period never starts in the past of
    /// the previous end, and a late renewal starts today, so no free back-dated period is created.
    /// </summary>
    public void Renew(DateTime nowUtc)
    {
        if (PaymentStatus != SubscriptionPaymentStatus.Paid)
        {
            throw new BusinessRuleException(
                "subscription.current_period_unpaid", "The current period must be paid in full before the subscription is renewed.");
        }

        var start = PeriodEndUtc;
        if (start.AddMonths((int)BillingPeriod) <= nowUtc)
        {
            start = nowUtc;
        }

        StartPeriod(start);
    }

    public void Supersede() => Status = SubscriptionStatus.Superseded;

    public void MarkRenewalNoticeSent() => RenewalNoticeForPeriodEndUtc = PeriodEndUtc;

    public void MarkOverdueNoticeSent() => OverdueNoticeForPeriodEndUtc = PeriodEndUtc;

    private void StartPeriod(DateTime startUtc)
    {
        PeriodStartUtc = startUtc;
        PeriodEndUtc = startUtc.AddMonths((int)BillingPeriod);
        PaidAmount = 0;
        PaidAtUtc = null;
        PaymentStatus = Fee == 0 ? SubscriptionPaymentStatus.Paid : SubscriptionPaymentStatus.Unpaid;
        if (Fee == 0)
        {
            PaidAtUtc = startUtc;
        }
    }
}

/// <summary>
/// One amount received from a tenant for its subscription. This is the platform's SaaS revenue, kept apart from the
/// brand's own sales revenue (BR-09, FR-08).
/// </summary>
public sealed class SubscriptionPayment : Entity<Guid>
{
    public const int MethodMaxLength = 30;
    public const int ReferenceMaxLength = 100;

    private SubscriptionPayment()
    {
    }

    public SubscriptionPayment(
        Guid subscriptionId, Guid tenantId, decimal amount, DateTime paidAtUtc, string method, string? reference, DateTime periodStartUtc,
        Guid? recordedBy)
    {
        Id = Guid.NewGuid();
        SubscriptionId = subscriptionId;
        TenantId = tenantId;
        Amount = amount;
        PaidAtUtc = paidAtUtc;
        Method = method.Trim();
        Reference = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim();
        PeriodStartUtc = periodStartUtc;
        RecordedBy = recordedBy;
    }

    public Guid SubscriptionId { get; private set; }

    public Guid TenantId { get; private set; }

    public decimal Amount { get; private set; }

    public DateTime PaidAtUtc { get; private set; }

    /// <summary>BankTransfer, Cash, ... free text chosen by the Super Admin.</summary>
    public string Method { get; private set; } = string.Empty;

    public string? Reference { get; private set; }

    /// <summary>Start of the billing period this payment is for.</summary>
    public DateTime PeriodStartUtc { get; private set; }

    public Guid? RecordedBy { get; private set; }
}
