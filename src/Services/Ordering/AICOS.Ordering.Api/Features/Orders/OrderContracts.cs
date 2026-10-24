namespace AICOS.Ordering.Api.Features.Orders;

public sealed record CreateOrderRequest(Guid RequestId, string OrderType, Guid? SessionId, DateTime? PickupTime, IReadOnlyList<CreateOrderItemRequest> Items);
public sealed record CreateOrderItemRequest(Guid VariantId, int Quantity, string? Note, IReadOnlyList<Guid> OptionIds);
public sealed record SessionOrdersResponse(Guid SessionId, IReadOnlyList<OrderResponse> Orders);
public sealed record OrderResponse(Guid Id, string OrderCode, string OrderType, string Status, Guid? SessionId, DateTime? PickupTime, decimal Subtotal, decimal TotalAmount, DateTime? CreatedAt, IReadOnlyList<OrderItemResponse> Items);
public sealed record OrderItemResponse(Guid Id, Guid VariantId, string ProductName, string? VariantName, int Quantity, decimal UnitPrice, string Status, string? Note, IReadOnlyList<OrderOptionResponse> Options, Guid? CreatedByUserId);
public sealed record OrderOptionResponse(Guid? OptionId, string Name, decimal ExtraPrice);
