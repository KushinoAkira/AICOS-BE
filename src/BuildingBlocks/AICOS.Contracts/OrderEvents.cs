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

/// <summary>
/// Published when an order is cancelled. <see cref="WasPaid"/> tells whether money has to go back (an exception, handled with a refund).
/// Customer Service uses <see cref="PromotionCode"/> to give an unused voucher back (unpaid order) and <see cref="CustomerId"/> and <see cref="TotalAmount"/> to take the points back (paid order).
/// </summary>
public record OrderCancelledIntegrationEvent(
    Guid TenantId,
    Guid OrderId,
    Guid BranchId,
    Guid? TableId,
    string Reason,
    bool WasPaid,
    IReadOnlyList<OrderItemDto> Items,
    DateTime CancelledAtUtc,
    string? OrderCode = null,
    Guid? CustomerId = null,
    string? PromotionCode = null,
    decimal TotalAmount = 0,
    string? ClosedAs = null) : IntegrationEvent;

/// <summary>
/// Published by Order Service once the money for an order is confirmed. Customer Service awards loyalty points from it (SCRUM-133, BR-25):
/// only paid orders earn points, and <see cref="CustomerId"/> is empty for a walk-in who is not a member.
/// </summary>
public record OrderPaidIntegrationEvent(
    Guid TenantId,
    Guid OrderId,
    string OrderCode,
    Guid BranchId,
    Guid? CustomerId,
    decimal Amount,
    DateTime PaidAtUtc) : IntegrationEvent;

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

/// <summary>
/// Published by Kitchen Service when the bar makes a ready order again (spilled, wrong drink, quality). <see cref="RemakeCount"/> is how many times
/// this ticket was remade, so a repeated message changes nothing. Order Service shows the order as Preparing again.
/// </summary>
public record KitchenOrderRemakeIntegrationEvent(
    Guid TenantId,
    Guid TicketId,
    Guid OrderId,
    Guid BranchId,
    string Reason,
    int RemakeCount,
    DateTime RemadeAtUtc) : IntegrationEvent;

/// <summary>
/// Published by Order Service whenever something happens to an order that its customer wants to hear about. Customer Service turns it into a
/// notification for a member (<see cref="CustomerId"/> is empty for a guest, who follows the order live instead). <see cref="Kind"/> is one of
/// Paid, Ready, Completed, Cancelled, NoShow, Unfulfillable, Remake. <see cref="DedupeKey"/> makes a repeated message create one notification.
/// </summary>
public record OrderStatusChangedIntegrationEvent(
    Guid TenantId,
    Guid OrderId,
    string OrderCode,
    Guid BranchId,
    Guid CustomerId,
    string Kind,
    string DedupeKey,
    string? TableNumber,
    decimal TotalAmount,
    DateTime AtUtc) : IntegrationEvent;

/// <summary>The words shown to the customer for each change of an order: one place, used for the live message and for the stored notification.</summary>
public static class OrderStatusTexts
{
    public static (string Title, string Body) For(string kind, string orderCode, string? tableNumber)
    {
        var at = string.IsNullOrWhiteSpace(tableNumber) ? string.Empty : $" tại bàn {tableNumber}";
        return kind switch
        {
            "Created" => ("Đã nhận đơn", $"Đơn {orderCode}{at} đã được gửi tới quầy."),
            "Paid" => ("Đã thanh toán", $"Đơn {orderCode} đã được thanh toán, quầy sẽ làm ngay."),
            "Preparing" => ("Đang pha chế", $"Đơn {orderCode}{at} đang được pha chế."),
            "Ready" => ("Đơn đã sẵn sàng", $"Đơn {orderCode}{at} đã xong, mời bạn nhận món."),
            "Remake" => ("Đang pha lại", $"Quầy đang pha lại đơn {orderCode}{at}, xin lỗi vì sự chậm trễ."),
            "Delivered" => ("Đã giao món", $"Đơn {orderCode}{at} đã được giao."),
            "Completed" => ("Hoàn tất", $"Đơn {orderCode} đã hoàn tất. Cảm ơn bạn, hãy đánh giá đơn nhé."),
            "Cancelled" => ("Đơn đã hủy", $"Đơn {orderCode} đã được hủy."),
            "NoShow" => ("Đơn quá giờ nhận", $"Đơn {orderCode} đã đóng vì quá giờ nhận hàng."),
            "Unfulfillable" => ("Quán không làm được đơn", $"Rất tiếc, quán không làm được đơn {orderCode}. Nếu đã thanh toán, tiền sẽ được hoàn lại."),
            _ => ("Cập nhật đơn", $"Đơn {orderCode} có cập nhật mới."),
        };
    }
}
