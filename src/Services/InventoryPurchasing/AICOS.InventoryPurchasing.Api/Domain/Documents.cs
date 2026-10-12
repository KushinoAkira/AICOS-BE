using AICOS.BuildingBlocks.Domain;
using AICOS.BuildingBlocks.Exceptions;

namespace AICOS.InventoryPurchasing.Api.Domain;

public enum PurchaseOrderStatus
{
    PendingApproval,
    Approved,
    Rejected,
    Received,
    Cancelled,
}

/// <summary>
/// Purchase order to a supplier (UC-22 create, UC-09 approve). BR-12: it must be approved by a Manager before it
/// is sent/received. Receiving it imports the goods into stock.
/// </summary>
public sealed class PurchaseOrder : AuditableEntity<Guid>, IBranchScoped
{
    private readonly List<PurchaseOrderItem> _items = [];

    private PurchaseOrder()
    {
    }

    public PurchaseOrder(Guid branchId, Guid supplierId, string code, DateOnly? expectedDate, string? note, IEnumerable<PurchaseOrderItem> items)
    {
        Id = Guid.NewGuid();
        BranchId = branchId;
        Code = code;
        Status = PurchaseOrderStatus.PendingApproval;
        Update(supplierId, expectedDate, note, items);
    }

    public Guid BranchId { get; private set; }

    /// <summary>Human-readable number printed on documents, e.g. PO-20261012-4F2A.</summary>
    public string Code { get; private set; } = string.Empty;

    public Guid SupplierId { get; private set; }

    public PurchaseOrderStatus Status { get; private set; }

    public DateOnly? ExpectedDate { get; private set; }

    public string? Note { get; private set; }

    public string? RejectReason { get; private set; }

    public decimal TotalAmount { get; private set; }

    public Guid? DecidedBy { get; private set; }

    public DateTime? DecidedAtUtc { get; private set; }

    public DateTime? ReceivedAtUtc { get; private set; }

    public IReadOnlyList<PurchaseOrderItem> Items => _items;

    public void Update(Guid supplierId, DateOnly? expectedDate, string? note, IEnumerable<PurchaseOrderItem> items)
    {
        EnsureStatus(PurchaseOrderStatus.PendingApproval, "edited");
        SupplierId = supplierId;
        ExpectedDate = expectedDate;
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        _items.Clear();
        _items.AddRange(items);
        TotalAmount = _items.Sum(i => i.Quantity * i.UnitPrice);
    }

    public void Approve(Guid? managerId, DateTime nowUtc)
    {
        EnsureStatus(PurchaseOrderStatus.PendingApproval, "approved");
        Status = PurchaseOrderStatus.Approved;
        DecidedBy = managerId;
        DecidedAtUtc = nowUtc;
    }

    public void Reject(Guid? managerId, string reason, DateTime nowUtc)
    {
        EnsureStatus(PurchaseOrderStatus.PendingApproval, "rejected");
        Status = PurchaseOrderStatus.Rejected;
        RejectReason = reason.Trim();
        DecidedBy = managerId;
        DecidedAtUtc = nowUtc;
    }

    public void MarkReceived(DateTime nowUtc)
    {
        EnsureStatus(PurchaseOrderStatus.Approved, "received");
        Status = PurchaseOrderStatus.Received;
        ReceivedAtUtc = nowUtc;
    }

    public void Cancel()
    {
        if (Status is not (PurchaseOrderStatus.PendingApproval or PurchaseOrderStatus.Approved))
        {
            throw Invalid("cancelled");
        }

        Status = PurchaseOrderStatus.Cancelled;
    }

    private void EnsureStatus(PurchaseOrderStatus expected, string action)
    {
        if (Status != expected)
        {
            throw Invalid(action);
        }
    }

    private BusinessRuleException Invalid(string action) =>
        new("purchase_order.invalid_status", $"A purchase order in status {Status} cannot be {action}.");
}

public sealed class PurchaseOrderItem : Entity<Guid>
{
    private PurchaseOrderItem()
    {
    }

    public PurchaseOrderItem(Guid ingredientId, decimal quantity, decimal unitPrice)
    {
        Id = Guid.NewGuid();
        IngredientId = ingredientId;
        Quantity = quantity;
        UnitPrice = unitPrice;
    }

    public Guid PurchaseOrderId { get; private set; }

    public Guid IngredientId { get; private set; }

    public decimal Quantity { get; private set; }

    public decimal UnitPrice { get; private set; }
}

public enum AdjustmentStatus
{
    PendingApproval,
    Approved,
    Rejected,
}

/// <summary>
/// Physical stock count (UC-21). Records system vs counted quantity; BR-15: stock only changes after a Manager approves.
/// </summary>
public sealed class StockAdjustment : AuditableEntity<Guid>, IBranchScoped
{
    private readonly List<StockAdjustmentLine> _lines = [];

    private StockAdjustment()
    {
    }

    public StockAdjustment(Guid branchId, string code, string? note, IEnumerable<StockAdjustmentLine> lines)
    {
        Id = Guid.NewGuid();
        BranchId = branchId;
        Code = code;
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        Status = AdjustmentStatus.PendingApproval;
        _lines.AddRange(lines);
    }

    public Guid BranchId { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public string? Note { get; private set; }

    public AdjustmentStatus Status { get; private set; }

    public string? RejectReason { get; private set; }

    public Guid? DecidedBy { get; private set; }

    public DateTime? DecidedAtUtc { get; private set; }

    public IReadOnlyList<StockAdjustmentLine> Lines => _lines;

    public void Approve(Guid? managerId, DateTime nowUtc)
    {
        EnsurePending("approved");
        Status = AdjustmentStatus.Approved;
        DecidedBy = managerId;
        DecidedAtUtc = nowUtc;
    }

    public void Reject(Guid? managerId, string reason, DateTime nowUtc)
    {
        EnsurePending("rejected");
        Status = AdjustmentStatus.Rejected;
        RejectReason = reason.Trim();
        DecidedBy = managerId;
        DecidedAtUtc = nowUtc;
    }

    private void EnsurePending(string action)
    {
        if (Status != AdjustmentStatus.PendingApproval)
        {
            throw new BusinessRuleException("stock_adjustment.invalid_status", $"A stock count in status {Status} cannot be {action}.");
        }
    }
}

public sealed class StockAdjustmentLine : Entity<Guid>
{
    private StockAdjustmentLine()
    {
    }

    public StockAdjustmentLine(Guid ingredientId, decimal systemQuantity, decimal countedQuantity)
    {
        Id = Guid.NewGuid();
        IngredientId = ingredientId;
        SystemQuantity = systemQuantity;
        CountedQuantity = countedQuantity;
    }

    public Guid StockAdjustmentId { get; private set; }

    public Guid IngredientId { get; private set; }

    /// <summary>Stock the system had when the count was submitted.</summary>
    public decimal SystemQuantity { get; private set; }

    public decimal CountedQuantity { get; private set; }

    public decimal Difference => CountedQuantity - SystemQuantity;
}

/// <summary>Spoiled / wasted ingredient (UC-35). BR-31: quantity cannot exceed what is in stock. Deducted immediately.</summary>
public sealed class WasteReport : AuditableEntity<Guid>, IBranchScoped
{
    private WasteReport()
    {
    }

    public WasteReport(Guid branchId, string code, Guid ingredientId, decimal quantity, string reason, string? evidenceImageUrl)
    {
        Id = Guid.NewGuid();
        BranchId = branchId;
        Code = code;
        IngredientId = ingredientId;
        Quantity = quantity;
        Reason = reason.Trim();
        EvidenceImageUrl = string.IsNullOrWhiteSpace(evidenceImageUrl) ? null : evidenceImageUrl.Trim();
    }

    public Guid BranchId { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public Guid IngredientId { get; private set; }

    public decimal Quantity { get; private set; }

    public string Reason { get; private set; } = string.Empty;

    public string? EvidenceImageUrl { get; private set; }
}
