namespace AICOS.Contracts.Events;

/// <summary>
/// Published by Payment Service when money for an order has been received (online provider confirmed, or cash taken at the counter).
/// Order Service uses it to mark the order paid and release it to the barista queue.
/// </summary>
public sealed record PaymentSucceededEvent(
    Guid TenantId, Guid BranchId, Guid PaymentId, Guid OrderId, decimal Amount, string Method, DateTime PaidAtUtc) : IntegrationEvent;

/// <summary>Published when an online payment failed, was cancelled or expired without payment. <c>TimedOut</c> is true when the time ran out: Order Service then releases the order and its slot (BR-25, BR-60); a plain decline leaves the order open for another try.</summary>
public sealed record PaymentFailedEvent(
    Guid TenantId, Guid BranchId, Guid PaymentId, Guid OrderId, string Method, string Reason, bool TimedOut = false) : IntegrationEvent;

/// <summary>Published when a refund was paid back to the customer. <see cref="Amount"/> is this refund only; <see cref="TotalRefunded"/> is all refunds of the payment.</summary>
public sealed record PaymentRefundedEvent(
    Guid TenantId, Guid BranchId, Guid PaymentId, Guid RefundId, Guid OrderId, decimal Amount, decimal TotalRefunded, string Method) : IntegrationEvent;

/// <summary>Published when a refund was refused by the approver or the provider could not pay it. Order Service shows the order as RefundFailed.</summary>
public sealed record PaymentRefundFailedEvent(
    Guid TenantId, Guid BranchId, Guid PaymentId, Guid RefundId, Guid OrderId, string Reason) : IntegrationEvent;
