using AICOS.BuildingBlocks.Domain;

namespace AICOS.Ordering.Api.Domain;

public enum TableSessionStatus
{
    Open = 1,
    Closed = 2,
}

/// <summary>Why a table session ended: everything was paid, staff closed it, or nobody used it for too long.</summary>
public enum TableSessionCloseReason
{
    Paid = 1,
    Staff = 2,
    Expired = 3,
}

/// <summary>
/// One sitting at a table (QR ordering). Everyone who scans the table QR joins the same open session, orders in several rounds, and the table
/// gets one bill. It belongs to one brand and branch, and ends when all its orders are paid, when staff close it, or after a period without activity.
/// </summary>
public sealed class TableSession : AuditableEntity<Guid>
{
    private TableSession()
    {
    }

    public TableSession(Guid tenantId, Guid branchId, Guid tableId, string tableCode, DateTime now)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        BranchId = branchId;
        TableId = tableId;
        TableCode = tableCode;
        Status = TableSessionStatus.Open;
        OpenedAtUtc = now;
        LastActivityAtUtc = now;
    }

    public Guid TenantId { get; private set; }

    public Guid BranchId { get; private set; }

    public Guid TableId { get; private set; }

    public string TableCode { get; private set; } = string.Empty;

    public TableSessionStatus Status { get; private set; }

    public DateTime OpenedAtUtc { get; private set; }

    public DateTime LastActivityAtUtc { get; private set; }

    public DateTime? ClosedAtUtc { get; private set; }

    public TableSessionCloseReason? CloseReason { get; private set; }

    /// <summary>The guests asked staff to come for the bill; cleared when the session ends.</summary>
    public DateTime? BillRequestedAtUtc { get; private set; }

    /// <summary>How the guests said they want to pay: Cash or Transfer.</summary>
    public string? BillRequestedMethod { get; private set; }

    public bool IsOpen => Status == TableSessionStatus.Open;

    public void Touch(DateTime now)
    {
        if (IsOpen && now > LastActivityAtUtc)
        {
            LastActivityAtUtc = now;
        }
    }

    public void RequestBill(string method, DateTime now)
    {
        BillRequestedMethod = method;
        BillRequestedAtUtc = now;
        Touch(now);
    }

    public void TransferTo(Guid tableId, string tableCode, DateTime now)
    {
        if (!IsOpen) throw new InvalidOperationException("Cannot move a closed session.");
        TableId = tableId;
        TableCode = tableCode;
        Touch(now);
    }

    public void Close(TableSessionCloseReason reason, DateTime now)
    {
        if (!IsOpen)
        {
            return;
        }

        Status = TableSessionStatus.Closed;
        CloseReason = reason;
        ClosedAtUtc = now;
    }
}
