namespace AICOS.Contracts.Events;

public sealed record OrderLineDto(
    Guid MenuItemId,
    int Quantity,
    string? SugarLevel,
    string? IceLevel,
    IReadOnlyList<Guid> ToppingIds);

/// <summary>Published by Order Service. Consumed by Kitchen Operations (to build the FIFO queue).</summary>
public sealed record OrderCreatedEvent(
    Guid OrderId,
    Guid BranchId,
    string OrderType,
    Guid? TableId,
    Guid? CustomerId,
    IReadOnlyList<OrderLineDto> Lines) : IntegrationEvent;

/// <summary>Published by Order Service. Consumed by Reporting, Customer Engagement (loyalty points), AI Service.</summary>
public sealed record OrderCompletedEvent(
    Guid OrderId,
    Guid BranchId,
    Guid? CustomerId,
    decimal TotalAmount,
    IReadOnlyList<OrderLineDto> Lines) : IntegrationEvent;
