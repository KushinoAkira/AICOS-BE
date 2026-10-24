namespace AICOS.KitchenOperations.Api.Domain.Entities;

/// <summary>
/// Thể hiện tem/sticker dán lên từng ly nước (UC_KITCHEN_02 / BR-43)
/// </summary>
public class CupSticker
{
    public Guid Id { get; private set; }
    public Guid TicketId { get; private set; }
    public Guid OrderId { get; private set; }
    public string OrderCode { get; private set; } = string.Empty;
    public string? TableNumber { get; private set; }
    public string ProductName { get; private set; } = string.Empty;
    
    /// <summary>Thứ tự ly trong đơn, ví dụ: 1/3, 2/3, 3/3</summary>
    public int CupIndex { get; private set; }
    public int TotalCupsInOrder { get; private set; }
    
    /// <summary>Tùy chọn chi tiết: lượng đường, đá, topping, ghi chú</summary>
    public string? Customizations { get; private set; }
    public DateTime PrintedAtUtc { get; private set; }

    protected CupSticker() { }

    public CupSticker(
        Guid ticketId,
        Guid orderId,
        string orderCode,
        string? tableNumber,
        string productName,
        int cupIndex,
        int totalCupsInOrder,
        string? customizations)
    {
        Id = Guid.NewGuid();
        TicketId = ticketId;
        OrderId = orderId;
        OrderCode = orderCode;
        TableNumber = tableNumber;
        ProductName = productName;
        CupIndex = cupIndex;
        TotalCupsInOrder = totalCupsInOrder;
        Customizations = customizations;
        PrintedAtUtc = DateTime.UtcNow;
    }
}
