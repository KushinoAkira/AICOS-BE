using AICOS.KitchenOperations.Api.Domain.Enums;

namespace AICOS.KitchenOperations.Api.Domain.Entities;

public class KitchenTicket
{
    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public string OrderCode { get; private set; } = string.Empty;
    public Guid BranchId { get; private set; }
    public string? TableNumber { get; private set; }
    public string OrderType { get; private set; } = string.Empty; // DineIn, Takeaway, Delivery
    public KitchenTicketStatus Status { get; private set; }
    public bool IsPaid { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }
    
    /// <summary>
    /// Giờ hẹn lấy món (dùng cho đơn Takeaway/Delivery đặt trước - BR-41 ưu tiên xử lý)
    /// </summary>
    public DateTime? ScheduledPickupTimeUtc { get; private set; }
    public bool IsPriority => ScheduledPickupTimeUtc.HasValue;

    public DateTime? BrewingStartedAtUtc { get; private set; }
    public DateTime? ReadyAtUtc { get; private set; }
    public DateTime? CompletedAtUtc { get; private set; }
    public DateTime? CancelledAtUtc { get; private set; }

    private readonly List<KitchenTicketItem> _items = new();
    public IReadOnlyCollection<KitchenTicketItem> Items => _items.AsReadOnly();

    private readonly List<CupSticker> _stickers = new();
    public IReadOnlyCollection<CupSticker> Stickers => _stickers.AsReadOnly();

    public int TotalCups => _items.Sum(i => i.Quantity);

    protected KitchenTicket() { }

    public KitchenTicket(
        Guid orderId,
        string orderCode,
        Guid branchId,
        string? tableNumber,
        string orderType,
        bool isPaid,
        DateTime? scheduledPickupTimeUtc = null)
    {
        Id = Guid.NewGuid();
        OrderId = orderId;
        OrderCode = orderCode;
        BranchId = branchId;
        TableNumber = tableNumber;
        OrderType = orderType;
        IsPaid = isPaid;
        ScheduledPickupTimeUtc = scheduledPickupTimeUtc;

        Status = KitchenTicketStatus.Pending;
        CreatedAtUtc = DateTime.UtcNow;
    }

    public void AddItem(Guid productId, string productName, int quantity, string? note = null)
    {
        if (Status != KitchenTicketStatus.Pending)
            throw new InvalidOperationException("Chỉ có thể thêm món khi đơn pha chế đang ở trạng thái Pending.");

        _items.Add(new KitchenTicketItem(Id, productId, productName, quantity, note));
    }

    /// <summary>
    /// Chuyển trạng thái sang "Đang pha chế"
    /// </summary>
    public void StartBrewing()
    {
        if (Status != KitchenTicketStatus.Pending)
            throw new InvalidOperationException("Chỉ đơn hàng đang chờ (Pending) mới có thể chuyển sang pha chế (Brewing).");

        Status = KitchenTicketStatus.Brewing;
        BrewingStartedAtUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Chuyển trạng thái sang "Sẵn sàng phục vụ / Ready for Pickup" (Cấp độ đơn hàng)
    /// </summary>
    public void MarkReadyForPickup()
    {
        if (Status != KitchenTicketStatus.Brewing && Status != KitchenTicketStatus.Pending)
            throw new InvalidOperationException("Chỉ đơn hàng đang pha chế mới có thể chuyển sang sẵn sàng phục vụ.");

        Status = KitchenTicketStatus.ReadyForPickup;
        ReadyAtUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Hoàn tất đơn hàng (Khách đã nhận)
    /// </summary>
    public void Complete()
    {
        if (Status != KitchenTicketStatus.ReadyForPickup)
            throw new InvalidOperationException("Chỉ đơn hàng đã sẵn sàng (Ready for Pickup) mới có thể chuyển sang Hoàn thành.");

        Status = KitchenTicketStatus.Completed;
        CompletedAtUtc = DateTime.UtcNow;
    }

    public void Cancel()
    {
        if (Status == KitchenTicketStatus.Completed)
            throw new InvalidOperationException("Không thể hủy đơn pha chế đã hoàn thành.");

        Status = KitchenTicketStatus.Cancelled;
        CancelledAtUtc = DateTime.UtcNow;
    }

    public void MarkAsPaid()
    {
        IsPaid = true;
    }

    /// <summary>
    /// Sinh tem dán ly nước tự động theo quy tắc BR-43:
    /// Tem chỉ được in sau khi đơn đã xác nhận thanh toán (Online) hoặc xác nhận tạo đơn (Dine-in).
    /// </summary>
    public bool TryGenerateCupStickers(out IReadOnlyList<CupSticker> generatedStickers)
    {
        // Kiểm tra điều kiện BR-43
        bool isEligibleForSticker = IsPaid || OrderType.Equals("DineIn", StringComparison.OrdinalIgnoreCase);

        if (!isEligibleForSticker)
        {
            generatedStickers = Array.Empty<CupSticker>();
            return false;
        }

        if (_stickers.Count > 0)
        {
            // Đã sinh tem trước đó
            generatedStickers = _stickers.AsReadOnly();
            return true;
        }

        int totalCups = TotalCups;
        int currentCupIndex = 1;

        foreach (var item in _items)
        {
            for (int i = 0; i < item.Quantity; i++)
            {
                var sticker = new CupSticker(
                    ticketId: Id,
                    orderId: OrderId,
                    orderCode: OrderCode,
                    tableNumber: TableNumber,
                    productName: item.ProductName,
                    cupIndex: currentCupIndex++,
                    totalCupsInOrder: totalCups,
                    customizations: item.Note
                );
                _stickers.Add(sticker);
            }
        }

        generatedStickers = _stickers.AsReadOnly();
        return true;
    }
}
