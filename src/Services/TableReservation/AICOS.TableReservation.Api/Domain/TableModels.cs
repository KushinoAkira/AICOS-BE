namespace AICOS.TableReservation.Api.Domain;

public static class CafeTableStatuses
{
    public const string Available = "AVAILABLE";
    public const string Occupied = "OCCUPIED";
    public const string Cleaning = "CLEANING";
}

public static class TableSessionStatuses
{
    public const string Open = "OPEN";
    public const string Closed = "CLOSED";
}

public sealed class TableArea
{
    private TableArea()
    {
    }

    public TableArea(Guid id, Guid branchId, string name, int sortOrder = 0)
    {
        Id = id;
        BranchId = branchId;
        Name = name.Trim();
        SortOrder = sortOrder;
    }

    public Guid Id { get; private set; }
    public Guid BranchId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public int? SortOrder { get; private set; }
    public ICollection<CafeTable> Tables { get; private set; } = [];
}

public sealed class CafeTable
{
    private CafeTable()
    {
    }

    public CafeTable(
        Guid id,
        Guid areaId,
        string tableNumber,
        int capacity,
        string qrToken)
    {
        Id = id;
        AreaId = areaId;
        TableNumber = tableNumber.Trim();
        Capacity = capacity;
        QrToken = qrToken.Trim();
        Status = CafeTableStatuses.Available;
    }

    public Guid Id { get; private set; }
    public Guid AreaId { get; private set; }
    public string TableNumber { get; private set; } = string.Empty;
    public int? Capacity { get; private set; }
    public string? Status { get; private set; } = CafeTableStatuses.Available;
    public string QrToken { get; private set; } = string.Empty;
    public TableArea Area { get; private set; } = null!;
    public ICollection<TableSessionTable> SessionLinks { get; private set; } = [];

    public void MarkOccupied() => Status = CafeTableStatuses.Occupied;

    public void MarkCleaning() => Status = CafeTableStatuses.Cleaning;

    public void MarkAvailable() => Status = CafeTableStatuses.Available;
}

public sealed class TableSession
{
    private TableSession()
    {
    }

    public TableSession(Guid id, Guid branchId, DateTime openedAt)
    {
        Id = id;
        BranchId = branchId;
        Status = TableSessionStatuses.Open;
        OpenedAt = openedAt;
    }

    public Guid Id { get; private set; }
    public Guid BranchId { get; private set; }
    public string? Status { get; private set; } = TableSessionStatuses.Open;
    public DateTime? OpenedAt { get; private set; }
    public DateTime? ClosedAt { get; private set; }
    public ICollection<TableSessionTable> TableLinks { get; private set; } = [];

    public void Close(DateTime closedAt)
    {
        if (Status != TableSessionStatuses.Open)
        {
            throw new InvalidOperationException("Only an open table session can be closed.");
        }

        Status = TableSessionStatuses.Closed;
        ClosedAt = closedAt;
    }
}

public sealed class TableSessionTable
{
    private TableSessionTable()
    {
    }

    public TableSessionTable(Guid id, Guid sessionId, Guid tableId, DateTime joinedAt)
    {
        Id = id;
        SessionId = sessionId;
        TableId = tableId;
        JoinedAt = joinedAt;
    }

    public Guid Id { get; private set; }
    public Guid SessionId { get; private set; }
    public Guid TableId { get; private set; }
    public DateTime? JoinedAt { get; private set; }
    public DateTime? LeftAt { get; private set; }
    public TableSession Session { get; private set; } = null!;
    public CafeTable Table { get; private set; } = null!;

    public void Leave(DateTime leftAt)
    {
        LeftAt ??= leftAt;
    }
}
