namespace AICOS.Contracts.Events;

/// <summary>Published by Payment Service once a gateway webhook (Momo/ZaloPay/VietQR) or cash payment is confirmed. Consumed by Order Service.</summary>
public sealed record PaymentConfirmedEvent(
    Guid PaymentId,
    Guid OrderId,
    Guid BranchId,
    decimal Amount,
    string Method) : IntegrationEvent;
