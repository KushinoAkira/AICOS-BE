namespace AICOS.Contracts.Events;

public record OrderItemDto(Guid ProductId, string ProductName, decimal UnitPrice, int Quantity, string? Note);

public record OrderCreatedIntegrationEvent(
    Guid OrderId,
    Guid BranchId,
    Guid? CashierId,
    Guid? CustomerId,
    Guid? TableId,
    string OrderType,
    decimal TotalAmount,
    IReadOnlyList<OrderItemDto> Items,
    DateTime CreatedAtUtc) : IntegrationEvent;

public record OrderCancelledIntegrationEvent(
    Guid OrderId,
    Guid BranchId,
    Guid? TableId,
    string Reason,
    IReadOnlyList<OrderItemDto> Items,
    DateTime CancelledAtUtc) : IntegrationEvent;

public record OrderCompletedIntegrationEvent(
    Guid OrderId,
    Guid BranchId,
    Guid? TableId,
    decimal TotalAmount,
    DateTime CompletedAtUtc) : IntegrationEvent;

public record PaymentCompletedIntegrationEvent(
    Guid PaymentId,
    Guid OrderId,
    decimal Amount,
    string PaymentMethod,
    DateTime PaidAtUtc) : IntegrationEvent;
