namespace AICOS.Contracts.Events;

/// <summary>
/// Published by the order / payment flow when a cashier takes cash for an order, or pays cash back (a refund of a cash order).
/// <see cref="Amount"/> is positive for cash received and negative for cash returned. Consumed by Workforce Service to compute the
/// expected amount of the cashier end-of-shift cash closing (UC-46, BR-55).
/// </summary>
public sealed record CashPaymentRecordedEvent(
    Guid TenantId, Guid BranchId, Guid CashierUserId, Guid? OrderId, decimal Amount, DateTime OccurredAtUtc) : IntegrationEvent;
