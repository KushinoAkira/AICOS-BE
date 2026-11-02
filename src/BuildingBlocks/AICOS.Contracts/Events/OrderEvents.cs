namespace AICOS.Contracts.Events;

public sealed record OrderLineDto(
    Guid MenuItemId,
    int Quantity,
    string? SugarLevel,
    string? IceLevel,
    IReadOnlyList<Guid> ToppingIds);

/// <summary>The guests of a table asked staff to come for the bill. Consumed by Kitchen Operations, which pushes it to the counter screens.</summary>
public sealed record TableBillRequestedIntegrationEvent(Guid TenantId, Guid BranchId, Guid SessionId, string TableCode, string Method) : IntegrationEvent;

/// <summary>Published by Order Service. Consumed by Kitchen Operations (to build the FIFO queue).</summary>
public sealed record OrderCreatedEvent(
    Guid OrderId,
    Guid BranchId,
    string OrderType,
    Guid? TableId,
    Guid? CustomerId,
    IReadOnlyList<OrderLineDto> Lines) : IntegrationEvent;

