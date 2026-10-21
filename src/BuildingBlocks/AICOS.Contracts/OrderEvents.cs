namespace AICOS.Contracts.Events;

public record OrderItemDto(Guid ProductId, string ProductName, decimal UnitPrice, int Quantity, string? Note);

/// <summary>
/// Published by Order Service when an order is placed. Kitchen turns it into a preparation ticket; Reporting records it.
/// <see cref="IsPaid"/> is true when the order was paid at creation. Drinks of a dine-in order are prepared before payment (BR-43), others wait for it.
/// </summary>
public record OrderCreatedIntegrationEvent(
    Guid TenantId,
    Guid OrderId,
    string OrderCode,
    Guid BranchId,
    Guid? CashierId,
    Guid? CustomerId,
    Guid? TableId,
    string? TableNumber,
    string OrderType,
    decimal TotalAmount,
    bool IsPaid,
    DateTime? ScheduledPickupTimeUtc,
    IReadOnlyList<OrderItemDto> Items,
    DateTime CreatedAtUtc) : IntegrationEvent;

/// <summary>Published when an order is cancelled. <see cref="WasPaid"/> tells whether money has to go back (an exception, handled with a refund).</summary>
public record OrderCancelledIntegrationEvent(
    Guid TenantId,
    Guid OrderId,
    Guid BranchId,
    Guid? TableId,
    string Reason,
    bool WasPaid,
    IReadOnlyList<OrderItemDto> Items,
    DateTime CancelledAtUtc) : IntegrationEvent;

/// <summary>Published when staff confirmed the customer received the order.</summary>
public record OrderCompletedIntegrationEvent(
    Guid TenantId,
    Guid OrderId,
    Guid BranchId,
    Guid? TableId,
    decimal TotalAmount,
    DateTime CompletedAtUtc) : IntegrationEvent;

/// <summary>Published by Kitchen Service when the last drink of an order is ready. Order Service marks the order Ready; the cashier is notified.</summary>
public record KitchenOrderReadyIntegrationEvent(
    Guid TenantId,
    Guid TicketId,
    Guid OrderId,
    string OrderCode,
    Guid BranchId,
    string? TableNumber,
    DateTime ReadyAtUtc) : IntegrationEvent;

/// <summary>Published by Kitchen Service when a barista starts making an order. Order Service shows it as Preparing.</summary>
public record KitchenOrderStartedIntegrationEvent(
    Guid TenantId,
    Guid TicketId,
    Guid OrderId,
    Guid BranchId,
    DateTime StartedAtUtc) : IntegrationEvent;
