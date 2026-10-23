using AICOS.BuildingBlocks.Domain;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.Ordering.Api.Domain.Enums;

namespace AICOS.Ordering.Api.Domain.Entities;

/// <summary>
/// An order placed at a branch (POS). It belongs to one brand (<see cref="TenantId"/>) and one branch. Money is handled by the Payment
/// service: this order only records what Payment reported (paid, refunded). Rule violations are <see cref="BusinessRuleException"/>s.
/// </summary>
public class Order : AuditableEntity<Guid>, IBranchScoped
{
    public const int NoteMaxLength = 500;

    public Guid TenantId { get; private set; }

    public string OrderCode { get; private set; } = string.Empty;

    public Guid BranchId { get; private set; }

    public Guid? CashierId { get; private set; }

    public Guid? CustomerId { get; private set; }

    public Guid? TableId { get; private set; }

    public string? TableNumber { get; private set; }

    public string? DeliveryAddress { get; private set; }

    public OrderType Type { get; private set; }

    public OrderStatus Status { get; private set; }

    public PaymentStatus PaymentStatus { get; private set; }

    /// <summary>The method the payer chose at the counter, or the one that actually paid once <see cref="PaymentStatus"/> is Paid.</summary>
    public PaymentMethod? PaymentMethod { get; private set; }

    /// <summary>The provider that took the money (Cash, Mock, VietQR, Momo, ZaloPay); used by reports.</summary>
    public string? PaymentProvider { get; private set; }

    /// <summary>The payment in the Payment service that paid this order.</summary>
    public Guid? PaymentId { get; private set; }

    public DateTime? PaidAtUtc { get; private set; }

    public DateTime? ReadyAtUtc { get; private set; }

    public DateTime? DeliveredAtUtc { get; private set; }

    public Guid? DeliveredBy { get; private set; }

    public DateTime? CompletedAtUtc { get; private set; }

    public DateTime? CancelledAtUtc { get; private set; }

    public string? CancelledReason { get; private set; }

    public string? Note { get; private set; }

    public string? PromotionCode { get; private set; }

    public decimal DiscountAmount { get; private set; }

    public decimal RefundedAmount { get; private set; }

    public RefundStatus RefundStatus { get; private set; }

    /// <summary>Who cancelled a paid order as an exception (the manager) and why; the money follows through a refund.</summary>
    public Guid? ExceptionBy { get; private set; }

    public bool IsPrinted { get; private set; }

    public int PrintedCount { get; private set; }

    public DateTime? LastPrintedAtUtc { get; private set; }

    private readonly List<OrderItem> _items = [];

    public IReadOnlyCollection<OrderItem> Items => _items.AsReadOnly();

    public decimal OriginalAmount => _items.Sum(i => i.TotalPrice);

    public decimal FinalAmount => Math.Max(0, OriginalAmount - DiscountAmount);

    public decimal TotalAmount => FinalAmount;

    public bool IsPaid => PaymentStatus is PaymentStatus.Paid or PaymentStatus.Refunded;

    protected Order()
    {
    }

    public Order(
        Guid tenantId,
        Guid branchId,
        OrderType type,
        DateTime now,
        Guid? cashierId = null,
        Guid? customerId = null,
        Guid? tableId = null,
        string? tableNumber = null,
        string? deliveryAddress = null,
        PaymentMethod? paymentMethod = null,
        string? note = null)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        OrderCode = $"ORD-{now:yyyyMMdd}-{Guid.NewGuid().ToString()[..6].ToUpperInvariant()}";
        BranchId = branchId;
        CashierId = cashierId;
        CustomerId = customerId;
        Type = type;
        TableId = type == OrderType.DineIn ? tableId : null;
        TableNumber = type == OrderType.DineIn ? tableNumber : null;
        DeliveryAddress = type == OrderType.Delivery ? deliveryAddress : null;
        PaymentMethod = paymentMethod;
        Note = note;
        Status = OrderStatus.Pending;
        PaymentStatus = PaymentStatus.Unpaid;
    }

    // ------------------------------------------------------------------ items and price
    public void AddItem(Guid productId, string productName, decimal unitPrice, int quantity, string? note = null)
    {
        if (Status is not (OrderStatus.Pending or OrderStatus.Confirmed))
        {
            throw new BusinessRuleException("order.not_editable", "Items can only be added while the order is pending or confirmed.");
        }

        var existing = _items.FirstOrDefault(i => i.ProductId == productId && i.Note == note);
        if (existing is not null)
        {
            existing.UpdateQuantity(existing.Quantity + quantity);
        }
        else
        {
            _items.Add(new OrderItem(Id, productId, productName, unitPrice, quantity, note));
        }
    }

    public void ApplyPromotion(string promoCode, decimal discountAmount)
    {
        if (Status != OrderStatus.Pending)
        {
            throw new BusinessRuleException("order.not_editable", "A promotion can only be applied to a pending order.");
        }

        PromotionCode = promoCode;
        DiscountAmount = Math.Min(Math.Max(0, discountAmount), OriginalAmount);
    }

    public void SwitchServiceType(OrderType newType, Guid? tableId = null, string? tableNumber = null, string? deliveryAddress = null)
    {
        if (Status is OrderStatus.Completed or OrderStatus.Cancelled)
        {
            throw new BusinessRuleException("order.closed", "The service type of a completed or cancelled order cannot change.");
        }

        Type = newType;
        TableId = newType == OrderType.DineIn ? tableId : null;
        TableNumber = newType == OrderType.DineIn ? tableNumber : null;
        DeliveryAddress = newType == OrderType.Delivery ? deliveryAddress : null;
    }

    // ------------------------------------------------------------------ preparation and hand-over
    /// <summary>The bar started making the order.</summary>
    public void MarkAsPreparing()
    {
        if (Status is OrderStatus.Pending or OrderStatus.Confirmed)
        {
            Status = OrderStatus.Preparing;
        }
    }

    /// <summary>The bar finished. Idempotent: a repeated or late message changes nothing.</summary>
    public void MarkAsReady(DateTime now)
    {
        if (Status is OrderStatus.Pending or OrderStatus.Confirmed or OrderStatus.Preparing)
        {
            Status = OrderStatus.Ready;
            ReadyAtUtc = now;
        }
    }

    /// <summary>Staff confirm the customer received the order (SCRUM-61). Completes the order when it is paid; an unpaid dine-in order completes when it is paid.</summary>
    public void Deliver(Guid? by, DateTime now)
    {
        if (Status == OrderStatus.Cancelled)
        {
            throw new BusinessRuleException("order.cancelled", "A cancelled order cannot be delivered.");
        }

        if (DeliveredAtUtc is not null)
        {
            throw new ConflictException("This order was already delivered.", "order.already_delivered");
        }

        if (Status != OrderStatus.Ready)
        {
            throw new BusinessRuleException("order.not_ready", $"Only a ready order can be delivered (this one is {Status}).");
        }

        DeliveredAtUtc = now;
        DeliveredBy = by;
        if (IsPaid)
        {
            Complete(now);
        }
    }

    private void Complete(DateTime now)
    {
        Status = OrderStatus.Completed;
        CompletedAtUtc = now;
    }

    // ------------------------------------------------------------------ cancellation (BR-04) and the exception path (FR-30)
    /// <summary>BR-04: an order can only be cancelled while it is not paid.</summary>
    public void CancelOrder(string reason, DateTime now)
    {
        if (Status == OrderStatus.Cancelled)
        {
            throw new ConflictException("This order is already cancelled.", "order.already_cancelled");
        }

        if (IsPaid)
        {
            throw new BusinessRuleException("order.paid", "BR-04: a paid order cannot be cancelled this way; ask a manager to handle it as an exception with a refund.");
        }

        if (Status == OrderStatus.Completed)
        {
            throw new BusinessRuleException("order.closed", "A completed order cannot be cancelled.");
        }

        Cancel(reason, now);
    }

    /// <summary>A manager cancels a paid order. The money goes back through a refund request that is tracked as <see cref="RefundStatus"/>.</summary>
    public void CancelPaidOrderAsException(string reason, Guid? by, DateTime now)
    {
        if (Status == OrderStatus.Cancelled)
        {
            throw new ConflictException("This order is already cancelled.", "order.already_cancelled");
        }

        if (!IsPaid)
        {
            throw new BusinessRuleException("order.not_paid", "Only a paid order is handled as an exception.");
        }

        Cancel(reason, now);
        ExceptionBy = by;
        // The refund can be paid back before this order is saved as cancelled (the refund event is fast): then it is already done.
        RefundStatus = RefundedAmount > 0 ? RefundStatus.Refunded : RefundStatus.RefundPending;
    }

    private void Cancel(string reason, DateTime now)
    {
        Status = OrderStatus.Cancelled;
        CancelledReason = string.IsNullOrWhiteSpace(reason) ? "Cancelled at the counter." : reason.Trim();
        CancelledAtUtc = now;
    }

    // ------------------------------------------------------------------ what the Payment service reports
    /// <summary>Payment confirmed the money. Idempotent. When the order was already cancelled the money has to be returned.</summary>
    public void MarkAsPaid(PaymentMethod method, string provider, Guid paymentId, DateTime paidAt)
    {
        if (IsPaid)
        {
            return;
        }

        PaymentStatus = PaymentStatus.Paid;
        PaymentMethod = method;
        PaymentProvider = provider;
        PaymentId = paymentId;
        PaidAtUtc = paidAt;

        if (Status == OrderStatus.Cancelled)
        {
            RefundStatus = RefundStatus.Needed;
        }
        else if (Status == OrderStatus.Pending)
        {
            Status = OrderStatus.Confirmed;
        }
        else if (Status == OrderStatus.Ready && DeliveredAtUtc is not null)
        {
            Complete(paidAt);
        }
    }

    /// <summary>Payment paid money back. A full refund also marks the order as refunded.</summary>
    public void ApplyRefund(decimal totalRefunded)
    {
        RefundedAmount = Math.Max(RefundedAmount, totalRefunded);
        if (RefundedAmount >= FinalAmount && FinalAmount > 0)
        {
            PaymentStatus = PaymentStatus.Refunded;
        }

        if (Status == OrderStatus.Cancelled)
        {
            RefundStatus = RefundStatus.Refunded;
        }
    }

    /// <summary>Payment refused or failed to pay the refund back.</summary>
    public void RefundFailed()
    {
        if (Status == OrderStatus.Cancelled && RefundStatus is RefundStatus.RefundPending or RefundStatus.Needed)
        {
            RefundStatus = RefundStatus.RefundFailed;
        }
    }

    /// <summary>Payment expired for an unpaid online order (BR-25): the order is released.</summary>
    public bool CancelBecausePaymentTimedOut(DateTime now)
    {
        if (IsPaid || Status is OrderStatus.Cancelled or OrderStatus.Completed)
        {
            return false;
        }

        Cancel("Not paid in time.", now);
        return true;
    }

    public void MarkAsPrinted(DateTime now)
    {
        IsPrinted = true;
        PrintedCount++;
        LastPrintedAtUtc = now;
    }
}
