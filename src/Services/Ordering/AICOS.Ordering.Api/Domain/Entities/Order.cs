using AICOS.Ordering.Api.Domain.Enums;

namespace AICOS.Ordering.Api.Domain.Entities;

public class Order
{
    public Guid Id { get; private set; }
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
    public PaymentMethod? PaymentMethod { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? CompletedAtUtc { get; private set; }
    public DateTime? CancelledAtUtc { get; private set; }
    public string? CancelledReason { get; private set; }
    
    public string? Note { get; private set; }
    public string? PromotionCode { get; private set; }
    public decimal DiscountAmount { get; private set; }

    // Dùng cho UC_POS_02: In / Gửi hóa đơn
    public bool IsPrinted { get; private set; }
    public int PrintedCount { get; private set; }
    public DateTime? LastPrintedAtUtc { get; private set; }

    // Navigation property
    private readonly List<OrderItem> _items = new();
    public IReadOnlyCollection<OrderItem> Items => _items.AsReadOnly();

    public decimal OriginalAmount => _items.Sum(i => i.TotalPrice);
    public decimal FinalAmount => Math.Max(0, OriginalAmount - DiscountAmount);

    // Compatibility property
    public decimal TotalAmount => FinalAmount;

    protected Order() { }

    public Order(
        Guid branchId,
        OrderType type,
        Guid? cashierId = null,
        Guid? customerId = null,
        Guid? tableId = null,
        string? tableNumber = null,
        string? deliveryAddress = null,
        PaymentMethod? paymentMethod = null,
        string? note = null)
    {
        Id = Guid.NewGuid();
        OrderCode = $"ORD-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString()[..6].ToUpper()}";
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
        CreatedAtUtc = DateTime.UtcNow;
        DiscountAmount = 0;
        IsPrinted = false;
        PrintedCount = 0;
    }

    // --- Domain Behaviors (Logic nghiệp vụ) ---

    public void AddItem(Guid productId, string productName, decimal unitPrice, int quantity, string? note = null)
    {
        if (Status != OrderStatus.Pending && Status != OrderStatus.Confirmed)
            throw new InvalidOperationException("Chỉ có thể thêm món khi đơn hàng đang chờ hoặc đã xác nhận.");

        var existingItem = _items.FirstOrDefault(i => i.ProductId == productId && i.Note == note);
        if (existingItem != null)
        {
            existingItem.UpdateQuantity(existingItem.Quantity + quantity);
        }
        else
        {
            _items.Add(new OrderItem(Id, productId, productName, unitPrice, quantity, note));
        }
    }

    public void RemoveItem(Guid orderItemId)
    {
        if (Status != OrderStatus.Pending)
            throw new InvalidOperationException("Chỉ có thể xóa món khi đơn hàng đang chờ xử lý.");

        var item = _items.FirstOrDefault(i => i.Id == orderItemId);
        if (item != null)
        {
            _items.Remove(item);
        }
    }

    /// <summary>
    /// Áp dụng mã khuyến mãi (Alternative Flow A1 của UC_POS_01)
    /// </summary>
    public void ApplyPromotion(string promoCode, decimal discountAmount)
    {
        if (Status != OrderStatus.Pending)
            throw new InvalidOperationException("Chỉ có thể áp dụng khuyến mãi khi đơn hàng đang chờ thanh toán.");

        if (discountAmount < 0)
            throw new ArgumentException("Số tiền giảm giá không được âm.", nameof(discountAmount));

        PromotionCode = promoCode;
        DiscountAmount = Math.Min(discountAmount, OriginalAmount);
    }

    /// <summary>
    /// Quản lý đổi bàn / đổi sang mang về / giao hàng (UC_POS_03)
    /// </summary>
    public void SwitchServiceType(OrderType newType, Guid? newTableId = null, string? newTableNumber = null, string? deliveryAddress = null)
    {
        if (Status == OrderStatus.Completed || Status == OrderStatus.Cancelled)
            throw new InvalidOperationException("Không thể đổi hình thức phục vụ cho đơn hàng đã hoàn tất hoặc đã hủy.");

        Type = newType;
        if (newType == OrderType.DineIn)
        {
            TableId = newTableId;
            TableNumber = newTableNumber;
            DeliveryAddress = null;
        }
        else if (newType == OrderType.Delivery)
        {
            TableId = null;
            TableNumber = null;
            DeliveryAddress = deliveryAddress;
        }
        else // Takeaway
        {
            TableId = null;
            TableNumber = null;
            DeliveryAddress = null;
        }
    }

    public void ConfirmOrder()
    {
        if (Status != OrderStatus.Pending)
            throw new InvalidOperationException("Không thể xác nhận đơn hàng ở trạng thái hiện tại.");
            
        Status = OrderStatus.Confirmed;
    }

    public void MarkAsPreparing()
    {
        if (Status != OrderStatus.Confirmed && Status != OrderStatus.Pending)
            throw new InvalidOperationException("Đơn hàng phải ở trạng thái xác nhận mới có thể chuyển sang chuẩn bị món.");
            
        Status = OrderStatus.Preparing;
    }

    public void MarkAsReady()
    {
        if (Status != OrderStatus.Preparing)
            throw new InvalidOperationException("Chỉ đơn hàng đang chuẩn bị mới có thể chuyển sang sẵn sàng.");
            
        Status = OrderStatus.Ready;
    }

    public void CompleteOrder()
    {
        if (Status == OrderStatus.Cancelled)
            throw new InvalidOperationException("Không thể hoàn thành đơn hàng đã bị hủy.");
            
        Status = OrderStatus.Completed;
        CompletedAtUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Hủy đơn hàng (UC_POS_04, BR-04)
    /// BR-04: Đơn hàng chỉ được hủy khi trạng thái chưa "Đã thanh toán"
    /// </summary>
    public void CancelOrder(string reason)
    {
        if (PaymentStatus == PaymentStatus.Paid)
            throw new InvalidOperationException("BR-04: Không thể hủy đơn hàng đã thanh toán thành công.");

        if (Status == OrderStatus.Completed)
            throw new InvalidOperationException("Không thể hủy đơn hàng đã hoàn thành.");

        if (Status == OrderStatus.Cancelled)
            throw new InvalidOperationException("Đơn hàng này đã bị hủy trước đó.");

        Status = OrderStatus.Cancelled;
        CancelledReason = string.IsNullOrWhiteSpace(reason) ? "Khách hủy tại quầy" : reason;
        CancelledAtUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Thanh toán thành công (UC_POS_01)
    /// </summary>
    public void MarkAsPaid(PaymentMethod method)
    {
        if (Status == OrderStatus.Cancelled)
            throw new InvalidOperationException("Không thể thanh toán đơn hàng đã bị hủy.");

        PaymentStatus = PaymentStatus.Paid;
        PaymentMethod = method;

        // Nếu đơn đang Pending thì tự động xác nhận chuyển sang làm món
        if (Status == OrderStatus.Pending)
        {
            Status = OrderStatus.Confirmed;
        }
    }

    /// <summary>
    /// Ghi nhận in hóa đơn (UC_POS_02)
    /// </summary>
    public void MarkAsPrinted()
    {
        IsPrinted = true;
        PrintedCount++;
        LastPrintedAtUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Kiểm tra quy tắc BR-09: Tự động hủy đơn thanh toán online sau timeoutMinutes (mặc định 5 phút)
    /// </summary>
    public bool IsOnlinePaymentExpired(int timeoutMinutes = 5)
    {
        if (PaymentStatus == PaymentStatus.Paid || Status == OrderStatus.Cancelled || Status == OrderStatus.Completed)
            return false;

        // Nếu phương thức là online (thẻ, ví, chuyển khoản) và chưa trả tiền
        bool isOnlineMethod = PaymentMethod is Enums.PaymentMethod.EWallet or Enums.PaymentMethod.Card or Enums.PaymentMethod.BankTransfer;
        if (!isOnlineMethod)
            return false;

        return (DateTime.UtcNow - CreatedAtUtc).TotalMinutes >= timeoutMinutes;
    }
}
