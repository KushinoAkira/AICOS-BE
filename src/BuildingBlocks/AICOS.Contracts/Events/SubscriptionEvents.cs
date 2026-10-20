namespace AICOS.Contracts.Events;

/// <summary>
/// Published by Identity Service when a tenant's renewal date is close (BR-10, FR-05), once per billing period.
/// To be consumed by the Notification service, which tells the brand's Brand Admin.
/// </summary>
public sealed record SubscriptionRenewalDueEvent(Guid TenantId, string TenantName, string PlanName, DateTime PeriodEndUtc, int DaysLeft)
    : IntegrationEvent;

/// <summary>
/// Published by Identity Service when a tenant did not renew in time, once per billing period (BR-10, BR-67).
/// <see cref="GracePeriodOver"/> is true when the Super Admin may now suspend the tenant.
/// </summary>
public sealed record SubscriptionOverdueEvent(
    Guid TenantId, string TenantName, string PlanName, DateTime PeriodEndUtc, int DaysOverdue, bool GracePeriodOver) : IntegrationEvent;
