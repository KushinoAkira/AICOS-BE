using System.Security.Cryptography;
using AICOS.BuildingBlocks.Domain;

namespace AICOS.TableReservation.Api.Domain;

public enum TableStatus
{
    Available,
    Occupied,

    /// <summary>Broken / not in use: its QR code does not open ordering.</summary>
    OutOfService,
}

/// <summary>A zone of a branch floor plan (UC-25), e.g. "Tầng 1", "Sân vườn". Tables are drawn inside it.</summary>
public sealed class TableArea : AuditableEntity<Guid>, IBranchScoped
{
    public const int NameMaxLength = 50;

    private TableArea()
    {
    }

    public TableArea(Guid tenantId, Guid branchId, string name, int sortOrder)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        BranchId = branchId;
        Update(name, sortOrder);
    }

    public Guid TenantId { get; private set; }

    public Guid BranchId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public int SortOrder { get; private set; }

    public void Update(string name, int sortOrder)
    {
        Name = name.Trim();
        SortOrder = sortOrder;
    }
}

/// <summary>
/// Physical table on a branch floor plan (UC-25). Each table has a QR token (UC-26) that identifies the table and its branch
/// when a guest scans the code; regenerating it invalidates the printed code (BR-18).
/// </summary>
public sealed class DiningTable : AuditableEntity<Guid>, IBranchScoped
{
    public const int CodeMaxLength = 20;

    private DiningTable()
    {
    }

    public DiningTable(Guid tenantId, Guid branchId, Guid areaId, string tableCode, int capacity, int? positionX, int? positionY, DateTime nowUtc)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        BranchId = branchId;
        Status = TableStatus.Available;
        RegenerateQr(nowUtc);
        Update(areaId, tableCode, capacity, positionX, positionY);
    }

    public Guid TenantId { get; private set; }

    public Guid BranchId { get; private set; }

    public Guid AreaId { get; private set; }

    /// <summary>Label printed on the table, e.g. "T01", "VIP-2". Unique per branch.</summary>
    public string TableCode { get; private set; } = string.Empty;

    public int Capacity { get; private set; }

    /// <summary>Optional coordinates for drawing the floor plan.</summary>
    public int? PositionX { get; private set; }

    public int? PositionY { get; private set; }

    public TableStatus Status { get; private set; }

    /// <summary>Random token encoded in the QR code. Regenerating it invalidates printed QR codes.</summary>
    public string QrToken { get; private set; } = string.Empty;

    public DateTime QrIssuedAtUtc { get; private set; }

    public void Update(Guid areaId, string tableCode, int capacity, int? positionX, int? positionY)
    {
        AreaId = areaId;
        TableCode = tableCode.Trim().ToUpperInvariant();
        Capacity = capacity;
        PositionX = positionX;
        PositionY = positionY;
    }

    public void ChangeStatus(TableStatus status) => Status = status;

    public void RegenerateQr(DateTime nowUtc)
    {
        QrToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        QrIssuedAtUtc = nowUtc;
    }
}
