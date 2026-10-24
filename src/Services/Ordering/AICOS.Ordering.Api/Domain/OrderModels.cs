namespace AICOS.Ordering.Api.Domain;

public static class OrderTypes
{
    public const string DineIn = "DINE_IN";
    public const string Takeaway = "TAKEAWAY";
}

public static class OrderStatuses
{
    public const string Pending = "PENDING";
}

public sealed class Order
{
    private Order() { }
    public Order(Guid id, Guid branchId, Guid? sessionId, string code, string type, DateTime? pickupTime, DateTime createdAt)
    {
        Id = id;
        BranchId = branchId;
        SessionId = sessionId;
        OrderCode = code;
        OrderType = type;
        PickupTime = pickupTime;
        Status = OrderStatuses.Pending;
        CreatedAt = createdAt;
    }
    public Guid Id { get; private set; }
    public Guid BranchId { get; private set; }
    public Guid? SessionId { get; private set; }
    public Guid? CustomerId { get; private set; }
    public string OrderCode { get; private set; } = string.Empty;
    public string OrderType { get; private set; } = string.Empty;
    public string? Status { get; private set; }
    public DateTime? PickupTime { get; private set; }
    public decimal? Subtotal { get; private set; }
    public decimal? TotalAmount { get; private set; }
    public DateTime? CreatedAt { get; private set; }
    public ICollection<OrderItem> Items { get; private set; } = [];
    public void SetTotals(decimal subtotal, decimal total) { Subtotal = subtotal; TotalAmount = total; }
}

public sealed class OrderItem
{
    private OrderItem() { }
    public OrderItem(Guid id, Guid orderId, Guid variantId, string productName, string variantName, int quantity, decimal unitPrice, string? note)
    {
        Id = id; OrderId = orderId; VariantId = variantId; ProductName = productName;
        VariantName = variantName; Quantity = quantity; UnitPrice = unitPrice; Status = OrderStatuses.Pending; Note = note;
    }
    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid VariantId { get; private set; }
    public string ProductName { get; private set; } = string.Empty;
    public string? VariantName { get; private set; }
    public int Quantity { get; private set; }
    public decimal UnitPrice { get; private set; }
    public string? Status { get; private set; }
    public string? Note { get; private set; }
    public Order Order { get; private set; } = null!;
    public ICollection<OrderItemOption> Options { get; private set; } = [];
    public ICollection<OrderItemEvent> Events { get; private set; } = [];
}

public sealed class OrderItemOption
{
    private OrderItemOption() { }
    public OrderItemOption(Guid id, Guid itemId, Guid optionId, string name, decimal extraPrice)
    { Id = id; OrderItemId = itemId; OptionId = optionId; OptionName = name; ExtraPrice = extraPrice; }
    public Guid Id { get; private set; }
    public Guid OrderItemId { get; private set; }
    public Guid? OptionId { get; private set; }
    public string OptionName { get; private set; } = string.Empty;
    public decimal ExtraPrice { get; private set; }
    public OrderItem OrderItem { get; private set; } = null!;
}

public sealed class OrderItemEvent
{
    private OrderItemEvent() { }
    public OrderItemEvent(Guid id, Guid itemId, Guid actorId, DateTime createdAt)
    { Id = id; OrderItemId = itemId; ActorUserId = actorId; EventType = "CREATED"; NewStatus = OrderStatuses.Pending; CreatedAt = createdAt; }
    public Guid Id { get; private set; }
    public Guid OrderItemId { get; private set; }
    public Guid? ActorUserId { get; private set; }
    public string EventType { get; private set; } = string.Empty;
    public string? OldStatus { get; private set; }
    public string? NewStatus { get; private set; }
    public string? Note { get; private set; }
    public DateTime? CreatedAt { get; private set; }
    public OrderItem OrderItem { get; private set; } = null!;
}
