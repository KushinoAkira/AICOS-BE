namespace AICOS.Contracts.Events;

/// <summary>
/// Published by Payment Service when money for an order has been received (online provider confirmed, or cash taken at the counter).
/// Order Service uses it to mark the order paid and release it to the barista queue.
/// </summary>
public sealed record PaymentSucceededEvent(
    Guid TenantId, Guid BranchId, Guid PaymentId, Guid OrderId, decimal Amount, string Method, DateTime PaidAtUtc) : IntegrationEvent;

/// <summary>Published when an online payment failed, was cancelled or expired without payment. Order Service releases the held slot (BR-25, BR-60).</summary>
public sealed record PaymentFailedEvent(
    Guid TenantId, Guid BranchId, Guid PaymentId, Guid OrderId, string Method, string Reason) : IntegrationEvent;

/// <summary>Published when a refund was paid back to the customer. <see cref="Amount"/> is this refund only; <see cref="TotalRefunded"/> is all refunds of the payment.</summary>
public sealed record PaymentRefundedEvent(
    Guid TenantId, Guid BranchId, Guid PaymentId, Guid RefundId, Guid OrderId, decimal Amount, decimal TotalRefunded, string Method) : IntegrationEvent;
