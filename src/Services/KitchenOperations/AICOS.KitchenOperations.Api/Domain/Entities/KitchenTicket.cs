using AICOS.BuildingBlocks.Domain;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.KitchenOperations.Api.Domain.Enums;
using AICOS.Contracts.Serving;
using AICOS.KitchenOperations.Api.Features.CupServing;

namespace AICOS.KitchenOperations.Api.Domain.Entities;

/// <summary>
/// A preparation ticket: one per order, created from the order event. It belongs to a brand and a branch. A takeaway or delivery order is
/// only shown to the bar once paid; a dine-in order is shown at once (BR-43).
/// </summary>
public class KitchenTicket : AuditableEntity<Guid>, IBranchScoped
{
    public Guid TenantId { get; private set; }
    public string FulfillmentMode { get; private set; } = FulfillmentModes.WholeOrder;
    public long FulfillmentVersion { get; private set; }
    public Guid? SourceTableId { get; private set; }
    public Guid? SourceSessionId { get; private set; }

    public void RequireWholeOrderOperation()
    {
        if (FulfillmentMode != FulfillmentModes.WholeOrder)
            throw new ConflictException("Use the cup workflow.", "order.cup_serving_required");
    }

    internal long AdvanceFulfillmentVersion() => ++FulfillmentVersion;

    internal void RecordCupProgress(IReadOnlyCollection<CupTask> cups, DateTime now)
    {
        if (FulfillmentMode != FulfillmentModes.CupV1 || cups.Count == 0 || cups.Count != TotalCups
            || cups.Any(c => c.TicketId != Id || c.TenantId != TenantId || c.BranchId != BranchId || c.OrderId != OrderId))
            throw new ConflictException("Cup set does not match its ticket.", "serving.identity_conflict");
        if (cups.All(c => c.Service == CupService.Served))
        {
            Status = KitchenTicketStatus.Completed; CompletedAtUtc = cups.Max(c => c.ServedAtUtc);
        }
        else if (cups.All(c => c.Preparation == CupPreparation.Ready))
        {
            Status = KitchenTicketStatus.ReadyForPickup; ReadyAtUtc = cups.Max(c => c.ReadyAtUtc);
        }
        else if (cups.Any(c => c.Preparation != CupPreparation.Pending))
        {
            Status = KitchenTicketStatus.Brewing; BrewingStartedAtUtc ??= now;
        }
    }

    public Guid OrderId { get; private set; }

    public string OrderCode { get; private set; } = string.Empty;

    public Guid BranchId { get; private set; }

    public string? TableNumber { get; private set; }

    /// <summary>DineIn, Takeaway or Delivery.</summary>
    public string OrderType { get; private set; } = string.Empty;

    public KitchenTicketStatus Status { get; private set; }

    public bool IsPaid { get; private set; }

    /// <summary>A guest ordered from the table QR code by themselves: this dine-in ticket waits for payment like a takeaway one.</summary>
    public bool PayFirst { get; private set; }

    public DateTime? ScheduledPickupTimeUtc { get; private set; }

    public bool IsPriority => ScheduledPickupTimeUtc.HasValue;

    public DateTime? BrewingStartedAtUtc { get; private set; }

    public DateTime? ReadyAtUtc { get; private set; }

    public DateTime? CompletedAtUtc { get; private set; }

    public DateTime? CancelledAtUtc { get; private set; }

    /// <summary>How many times the bar made this order again after it was ready (spilled, wrong drink, quality).</summary>
    public int RemakeCount { get; private set; }

    public string? LastRemakeReason { get; private set; }
    public string? RecoveryHoldReason { get; private set; }

    public void HoldRecovery(string reason) => RecoveryHoldReason = reason;
    public void ClearRecoveryHold() => RecoveryHoldReason = null;
    public void RequireRecoveryClear()
    {
        if (RecoveryHoldReason is not null)
            throw new ConflictException("Recovery review is required before preparation.", "ticket.recovery_review_required");
    }

    public DateTime? LastRemakeAtUtc { get; private set; }

    private readonly List<KitchenTicketItem> _items = [];

    public IReadOnlyCollection<KitchenTicketItem> Items => _items.AsReadOnly();

    private readonly List<CupSticker> _stickers = [];

    public IReadOnlyCollection<CupSticker> Stickers => _stickers.AsReadOnly();

    public int TotalCups => _items.Sum(i => i.Quantity);

    /// <summary>BR-43: the bar sees and prints stickers for a paid order, or a dine-in order a staff member keyed in. A guest's own QR order is paid first.</summary>
    public bool IsEligibleForBar => IsPaid || (OrderType.Equals("DineIn", StringComparison.OrdinalIgnoreCase) && !PayFirst);

    protected KitchenTicket()
    {
    }

    public KitchenTicket(
        Guid tenantId, Guid orderId, string orderCode, Guid branchId, string? tableNumber, string orderType, bool isPaid, DateTime? scheduledPickupTimeUtc = null,
        bool payFirst = false)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        OrderId = orderId;
        OrderCode = orderCode;
        BranchId = branchId;
        TableNumber = tableNumber;
        OrderType = orderType;
        IsPaid = isPaid;
        PayFirst = payFirst;
        ScheduledPickupTimeUtc = scheduledPickupTimeUtc;
        Status = KitchenTicketStatus.Pending;
    }

    public void AddItem(Guid productId, string productName, int quantity, string? note = null)
    {
        if (Status != KitchenTicketStatus.Pending)
        {
            throw new BusinessRuleException("ticket.not_pending", "Items can only be added while the ticket is pending.");
        }

        _items.Add(new KitchenTicketItem(Id, productId, productName, quantity, note));
    }

    public void TransferTable(string code) => TableNumber = code;

    public void StartBrewing(DateTime now)
    {
        RequireWholeOrderOperation();
        RequireRecoveryClear();
        if (Status != KitchenTicketStatus.Pending)
        {
            throw new BusinessRuleException("ticket.not_pending", "Only a pending ticket can start brewing.");
        }

        Status = KitchenTicketStatus.Brewing;
        BrewingStartedAtUtc = now;
    }

    public void MarkReadyForPickup(DateTime now)
    {
        RequireWholeOrderOperation();
        RequireRecoveryClear();
        if (Status is not (KitchenTicketStatus.Brewing or KitchenTicketStatus.Pending))
        {
            throw new BusinessRuleException("ticket.not_brewing", "Only a ticket being brewed can be marked ready.");
        }

        Status = KitchenTicketStatus.ReadyForPickup;
        ReadyAtUtc = now;
    }

    /// <summary>The bar makes a ready order again: it goes back to brewing and must be marked ready once more. Not possible after the hand-over.</summary>
    public void Remake(string reason, DateTime now)
    {
        RequireWholeOrderOperation();
        RequireRecoveryClear();
        if (Status != KitchenTicketStatus.ReadyForPickup)
        {
            throw new BusinessRuleException("ticket.not_ready", "Only a ready order can be made again; after the hand-over use the complaint path of the order.");
        }

        Status = KitchenTicketStatus.Brewing;
        BrewingStartedAtUtc = now;
        ReadyAtUtc = null;
        RemakeCount++;
        LastRemakeReason = reason.Trim();
        LastRemakeAtUtc = now;
    }

    public void ConfirmDelivered(DateTime deliveredAt)
    {
        RequireWholeOrderOperation();
        if (Status is KitchenTicketStatus.Completed or KitchenTicketStatus.Cancelled) return;
        Status = KitchenTicketStatus.Completed;
        CompletedAtUtc = deliveredAt;
    }

    public void Complete(DateTime now)
    {
        RequireWholeOrderOperation();
        if (Status != KitchenTicketStatus.ReadyForPickup)
        {
            throw new BusinessRuleException("ticket.not_ready", "Only a ready ticket can be completed.");
        }

        Status = KitchenTicketStatus.Completed;
        CompletedAtUtc = now;
    }

    public void Cancel(DateTime now)
    {
        RequireWholeOrderOperation();
        if (Status == KitchenTicketStatus.Completed)
        {
            throw new BusinessRuleException("ticket.completed", "A completed ticket cannot be cancelled.");
        }

        if (Status == KitchenTicketStatus.Cancelled)
        {
            return;
        }

        Status = KitchenTicketStatus.Cancelled;
        CancelledAtUtc = now;
    }

    /// <summary>The money arrived. Returns true when this made the ticket visible to the bar for the first time.</summary>
    public bool MarkAsPaid()
    {
        var wasEligible = IsEligibleForBar;
        IsPaid = true;
        return !wasEligible;
    }

    /// <summary>Creates one sticker per cup once the ticket is eligible. Idempotent: the same stickers are returned afterwards.</summary>
    public bool TryGenerateCupStickers(DateTime now, out IReadOnlyList<CupSticker> stickers)
    {
        RequireWholeOrderOperation();
        RequireRecoveryClear();
        if (!IsEligibleForBar)
        {
            stickers = [];
            return false;
        }

        if (_stickers.Count == 0)
        {
            var total = TotalCups;
            var index = 1;
            foreach (var item in _items)
            {
                for (var i = 0; i < item.Quantity; i++)
                {
                    _stickers.Add(new CupSticker(Id, OrderId, OrderCode, TableNumber, item.ProductName, index++, total, item.Note, now));
                }
            }
        }

        stickers = _stickers.AsReadOnly();
        return true;
    }
}

public class KitchenTicketItem : Entity<Guid>
{
    public Guid? OrderItemId { get; private set; }
    public Guid TicketId { get; private set; }

    public Guid ProductId { get; private set; }

    public string ProductName { get; private set; } = string.Empty;

    public int Quantity { get; private set; }

    public string? Note { get; private set; }

    protected KitchenTicketItem()
    {
    }

    public KitchenTicketItem(Guid ticketId, Guid productId, string productName, int quantity, string? note = null)
    {
        if (quantity <= 0)
        {
            throw new ArgumentException("Quantity must be greater than 0.", nameof(quantity));
        }

        Id = Guid.NewGuid();
        TicketId = ticketId;
        ProductId = productId;
        ProductName = productName;
        Quantity = quantity;
        Note = note;
    }
}

public class CupSticker : Entity<Guid>
{
    public Guid? CupTaskId { get; private set; }
    public Guid TicketId { get; private set; }

    public Guid OrderId { get; private set; }

    public string OrderCode { get; private set; } = string.Empty;

    public string? TableNumber { get; private set; }

    public string ProductName { get; private set; } = string.Empty;

    public int CupIndex { get; private set; }

    public int TotalCupsInOrder { get; private set; }

    public string? Customizations { get; private set; }

    public DateTime PrintedAtUtc { get; private set; }

    protected CupSticker()
    {
    }

    public CupSticker(
        Guid ticketId, Guid orderId, string orderCode, string? tableNumber, string productName, int cupIndex, int totalCupsInOrder, string? customizations, DateTime now)
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
        PrintedAtUtc = now;
    }
}
