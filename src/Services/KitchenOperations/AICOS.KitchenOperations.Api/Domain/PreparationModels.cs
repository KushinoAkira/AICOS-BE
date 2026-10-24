namespace AICOS.KitchenOperations.Api.Domain;

public static class PreparationStatuses
{
    public const string Pending = "PENDING";
    public const string Preparing = "PREPARING";
    public const string Ready = "READY";
}

public sealed class PreparationOrder
{
    private PreparationOrder()
    {
    }

    public PreparationOrder(
        Guid id,
        Guid branchId,
        string orderCode,
        string orderType,
        DateTime createdAt)
    {
        Id = id;
        BranchId = branchId;
        OrderCode = orderCode;
        OrderType = orderType;
        Status = PreparationStatuses.Pending;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public Guid BranchId { get; private set; }
    public string OrderCode { get; private set; } = string.Empty;
    public string OrderType { get; private set; } = string.Empty;
    public string? Status { get; private set; }
    public DateTime? CreatedAt { get; private set; }
    public ICollection<PreparationItem> Items { get; private set; } = [];
}

public sealed class PreparationItem
{
    private PreparationItem()
    {
    }

    public PreparationItem(
        Guid id,
        Guid orderId,
        string productName,
        string? variantName,
        int quantity,
        string? note)
    {
        Id = id;
        OrderId = orderId;
        ProductName = productName;
        VariantName = variantName;
        Quantity = quantity;
        Status = PreparationStatuses.Pending;
        Note = note;
    }

    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public string ProductName { get; private set; } = string.Empty;
    public string? VariantName { get; private set; }
    public int Quantity { get; private set; }
    public string? Status { get; private set; }
    public string? Note { get; private set; }
    public PreparationOrder Order { get; private set; } = null!;
    public ICollection<PreparationEvent> Events { get; private set; } = [];

    public void MoveTo(string nextStatus)
    {
        var current = Status ?? PreparationStatuses.Pending;
        var valid =
            current == PreparationStatuses.Pending && nextStatus == PreparationStatuses.Preparing ||
            current == PreparationStatuses.Preparing && nextStatus == PreparationStatuses.Ready;
        if (!valid)
        {
            throw new InvalidOperationException(
                $"Cannot move preparation item from {current} to {nextStatus}.");
        }

        Status = nextStatus;
    }
}

public sealed class PreparationEvent
{
    private PreparationEvent()
    {
    }

    public PreparationEvent(
        Guid id,
        Guid itemId,
        Guid actorId,
        string oldStatus,
        string newStatus,
        DateTime createdAt)
    {
        Id = id;
        OrderItemId = itemId;
        ActorUserId = actorId;
        EventType = "STATUS_CHANGED";
        OldStatus = oldStatus;
        NewStatus = newStatus;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public Guid OrderItemId { get; private set; }
    public Guid? ActorUserId { get; private set; }
    public string EventType { get; private set; } = string.Empty;
    public string? OldStatus { get; private set; }
    public string? NewStatus { get; private set; }
    public string? Note { get; private set; }
    public DateTime? CreatedAt { get; private set; }
    public PreparationItem Item { get; private set; } = null!;
}
