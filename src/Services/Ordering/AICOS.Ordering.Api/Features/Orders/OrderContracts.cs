using AICOS.Ordering.Api.Domain.Entities;
using AICOS.Ordering.Api.Domain.Enums;

namespace AICOS.Ordering.Api.Features.Orders;

/// <summary>
/// POST /orders. Only the product id, quantity and note of an item are read: the name and the price always come from the Menu service,
/// so a counter terminal (or anyone with a token) cannot set its own price.
/// </summary>
public sealed record CreateOrderRequest(
    Guid? BranchId,
    OrderType? Type,
    Guid? CustomerId,
    Guid? TableId,
    string? TableNumber,
    string? DeliveryAddress,
    PaymentMethod? PaymentMethod,
    string? PromotionCode,
    string? Note,
    List<OrderItemRequest>? Items,
    string? TableQrToken = null, Guid? ExpectedSessionId = null);

/// <summary><c>OptionIds</c>: the options chosen for the line (size, sugar, ice, toppings) from the product option groups of the menu.</summary>
public sealed record OrderItemRequest(Guid? ProductId, int? Quantity, string? Note, List<Guid>? OptionIds = null);

public sealed record SwitchServiceTypeRequest(OrderType? Type, Guid? TableId, string? TableNumber, string? DeliveryAddress);

public sealed record CancelOrderRequest(string? Reason);

/// <summary>POST /orders/{id}/no-show and /orders/{id}/unfulfillable: why the order is closed.</summary>
public sealed record OrderReasonRequest(string? Reason);

/// <summary>POST /orders/{id}/exception. <c>RefundKind</c> is Cancellation (default, the brand policy decides the amount), NoShow, or Exception (needs an amount).</summary>
public sealed record OrderExceptionRequest(string? Reason, string? RefundKind, decimal? Amount);

public sealed record OrderItemOptionResponse(Guid OptionId, string GroupName, string Name, decimal PriceDelta);

/// <summary><c>UnitPrice</c> includes the surcharge of the options; <c>BasePrice</c> is the product price without them.</summary>
public sealed record OrderItemResponse(
    Guid Id, Guid ProductId, string ProductName, decimal UnitPrice, int Quantity, decimal TotalPrice, string? Note, IReadOnlyList<OrderItemOptionResponse>? Options = null, decimal? BasePrice = null)
{
    public static OrderItemResponse From(OrderItem i) => new(
        i.Id, i.ProductId, i.ProductName, i.UnitPrice, i.Quantity, i.TotalPrice, i.Note,
        i.Options.Select(o => new OrderItemOptionResponse(o.OptionId, o.GroupName, o.Name, o.PriceDelta)).ToList(), i.UnitPrice - i.Options.Sum(o => o.PriceDelta));
}

public sealed record OrderResponse(
    Guid Id,
    string OrderCode,
    Guid BranchId,
    Guid? CashierId,
    Guid? CustomerId,
    OrderType Type,
    OrderStatus Status,
    PaymentStatus PaymentStatus,
    PaymentMethod? PaymentMethod,
    string? PaymentProvider,
    Guid? PaymentId,
    Guid? TableId,
    string? TableNumber,
    string? DeliveryAddress,
    string? Note,
    decimal OriginalAmount,
    string? PromotionCode,
    decimal DiscountAmount,
    decimal FinalAmount,
    decimal RefundedAmount,
    RefundStatus RefundStatus,
    DateTime CreatedAtUtc,
    DateTime? PaidAtUtc,
    DateTime? ReadyAtUtc,
    DateTime? DeliveredAtUtc,
    DateTime? CompletedAtUtc,
    DateTime? CancelledAtUtc,
    string? CancelledReason,
    IReadOnlyList<OrderItemResponse> Items,
    DateTime? PickupSlotStartUtc = null,
    DateTime? PaymentDueAtUtc = null,
    Guid? TableSessionId = null,
    int RemakeCount = 0,
    bool HasFeedback = false)
{
    public static OrderResponse From(Order o) => new(
        o.Id, o.OrderCode, o.BranchId, o.CashierId, o.CustomerId, o.Type, o.Status, o.PaymentStatus, o.PaymentMethod, o.PaymentProvider, o.PaymentId,
        o.TableId, o.TableNumber, o.DeliveryAddress, o.Note, o.OriginalAmount, o.PromotionCode, o.DiscountAmount, o.FinalAmount, o.RefundedAmount, o.EffectiveRefundStatus,
        o.CreatedAtUtc, o.PaidAtUtc, o.ReadyAtUtc, o.DeliveredAtUtc, o.CompletedAtUtc, o.CancelledAtUtc, o.CancelledReason,
        o.Items.Select(OrderItemResponse.From).ToList(),
        o.PickupSlotStartUtc, o.PaymentDueAtUtc, o.TableSessionId, o.RemakeCount);
}

public sealed record OrderSummary(
    Guid Id,
    string OrderCode,
    Guid BranchId,
    OrderType Type,
    OrderStatus Status,
    PaymentStatus PaymentStatus,
    string? PaymentProvider,
    string? TableNumber,
    decimal OriginalAmount,
    decimal DiscountAmount,
    decimal FinalAmount,
    decimal RefundedAmount,
    RefundStatus RefundStatus,
    int ItemCount,
    DateTime? PaidAtUtc,
    DateTime? CancelledAtUtc,
    DateTime CreatedAtUtc,
    DateTime? PickupSlotStartUtc = null,
    bool HasFeedback = false,
    IReadOnlyList<OrderItemResponse>? Items = null,
    Guid? CustomerId = null,
    DateTime? CompletedAtUtc = null,
    Guid? CashierId = null,
    Guid? TableId = null,
    DateTime? DeliveredAtUtc = null)
{
    public static OrderSummary From(Order o) => new OrderSummary(
        o.Id, o.OrderCode, o.BranchId, o.Type, o.Status, o.PaymentStatus, o.PaymentProvider, o.TableNumber, o.OriginalAmount, o.DiscountAmount, o.FinalAmount,
        o.RefundedAmount, o.EffectiveRefundStatus, o.Items.Count, o.PaidAtUtc, o.CancelledAtUtc, o.CreatedAtUtc, o.PickupSlotStartUtc) with { Items = o.Items.Select(OrderItemResponse.From).ToList(), CustomerId = o.CustomerId, CompletedAtUtc = o.CompletedAtUtc, CashierId = o.CashierId, TableId = o.TableId, DeliveredAtUtc = o.DeliveredAtUtc };
}

/// <summary>The result of checking an order's payment (SCRUM-60): what Payment reported, in one small answer.</summary>
public sealed record PaymentVerificationResponse(
    Guid OrderId, string OrderCode, decimal AmountDue, PaymentStatus PaymentStatus, PaymentMethod? Method, string? Provider, Guid? PaymentId, DateTime? PaidAtUtc,
    decimal RefundedAmount, bool IsPaid);

public sealed record ReceiptItemDto(string ProductName, decimal UnitPrice, int Quantity, decimal TotalPrice, string? Note, IReadOnlyList<string>? Options = null);

/// <summary>The bill (SCRUM-60). <c>IsReprint</c> is true from the second print on.</summary>
public sealed record ReceiptResponse(
    string ReceiptNumber,
    Guid OrderId,
    Guid BranchId,
    Guid? CashierId,
    OrderType OrderType,
    string? TableNumber,
    string? DeliveryAddress,
    DateTime CreatedAtUtc,
    IReadOnlyList<ReceiptItemDto> Items,
    decimal OriginalAmount,
    string? PromotionCode,
    decimal DiscountAmount,
    decimal FinalAmount,
    PaymentStatus PaymentStatus,
    string PaymentMethod,
    DateTime? PaidAtUtc,
    bool IsReprint,
    int PrintedCount,
    DateTime PrintedAtUtc)
{
    public static ReceiptResponse From(Order o, DateTime now) => new(
        o.OrderCode, o.Id, o.BranchId, o.CashierId, o.Type, o.TableNumber, o.DeliveryAddress, o.CreatedAtUtc,
        o.Items.Select(i => new ReceiptItemDto(i.ProductName, i.UnitPrice, i.Quantity, i.TotalPrice, i.Note, i.Options.Select(x => x.Name).ToList())).ToList(),
        o.OriginalAmount, o.PromotionCode, o.DiscountAmount, o.FinalAmount, o.PaymentStatus, o.PaymentProvider ?? o.PaymentMethod?.ToString() ?? "Not paid", o.PaidAtUtc,
        o.PrintedCount > 1, o.PrintedCount, o.LastPrintedAtUtc ?? now);
}

/// <summary>POST /orders/pickup (a customer orders takeaway for a pickup slot). The price always comes from Menu.</summary>
public sealed record CreatePickupOrderRequest(
    Guid? BranchId,
    DateTime? SlotStartUtc,
    string? PromotionCode,
    string? Note,
    List<OrderItemRequest>? Items);

/// <summary>One pickup slot of a day. <c>Available</c> is false when the slot is full or already locked by the cut-off (BR-22, BR-23).</summary>
public sealed record PickupSlotInfo(DateTime StartUtc, DateTime EndUtc, int Capacity, int Booked, int Remaining, bool Locked, bool Available);

public sealed record AvailablePickupSlotsResponse(Guid BranchId, DateOnly Date, int SlotMinutes, int CapacityPerSlot, int CutoffMinutes, IReadOnlyList<PickupSlotInfo> Slots);

/// <summary>POST /orders/{id}/feedback</summary>
public sealed record OrderFeedbackRequest(int? Rating, string? Comment);

public sealed record OrderFeedbackResponse(Guid Id, Guid OrderId, string OrderCode, Guid BranchId, Guid CustomerId, int Rating, string? Comment, DateTime CreatedAtUtc)
{
    public static OrderFeedbackResponse From(Domain.OrderFeedback f) => new(f.Id, f.OrderId, f.OrderCode, f.BranchId, f.CustomerId, f.Rating, f.Comment, f.CreatedAtUtc);
}
