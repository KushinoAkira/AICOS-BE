using System.Security.Cryptography;
using AICOS.BuildingBlocks.Domain;
using AICOS.BuildingBlocks.Exceptions;

namespace AICOS.TableReservation.Api.Domain;

public enum TableStatus
{
    Available,
    Occupied,

    /// <summary>Broken / not in use: cannot be reserved, its QR code does not open ordering.</summary>
    OutOfService,
}

/// <summary>Physical table on a branch floor plan (UC-38). Each table has a QR token for QR ordering (FR-59).</summary>
public sealed class DiningTable : AuditableEntity<Guid>, IBranchScoped
{
    public const int CodeMaxLength = 20;
    public const int AreaMaxLength = 50;

    private DiningTable()
    {
    }

    public DiningTable(Guid branchId, string tableCode, int capacity, string? area, int? positionX, int? positionY)
    {
        Id = Guid.NewGuid();
        BranchId = branchId;
        Status = TableStatus.Available;
        RegenerateQr();
        Update(tableCode, capacity, area, positionX, positionY);
    }

    public Guid BranchId { get; private set; }

    /// <summary>Label printed on the table, e.g. "T01", "VIP-2". Unique per branch.</summary>
    public string TableCode { get; private set; } = string.Empty;

    public int Capacity { get; private set; }

    /// <summary>Floor-plan zone, e.g. "Tầng 1", "Sân vườn".</summary>
    public string? Area { get; private set; }

    /// <summary>Optional coordinates for drawing the floor plan.</summary>
    public int? PositionX { get; private set; }

    public int? PositionY { get; private set; }

    public TableStatus Status { get; private set; }

    /// <summary>Random token encoded in the QR code. Regenerating it invalidates printed QR codes.</summary>
    public string QrToken { get; private set; } = string.Empty;

    public void Update(string tableCode, int capacity, string? area, int? positionX, int? positionY)
    {
        TableCode = tableCode.Trim().ToUpperInvariant();
        Capacity = capacity;
        Area = string.IsNullOrWhiteSpace(area) ? null : area.Trim();
        PositionX = positionX;
        PositionY = positionY;
    }

    public void ChangeStatus(TableStatus status) => Status = status;

    public void RegenerateQr() => QrToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
}

public enum ReservationStatus
{
    Pending,
    Confirmed,
    Seated,
    Completed,
    Cancelled,
    NoShow,
}

/// <summary>Table reservation (UC-29 online by a customer, UC-18 by a cashier). BR-10: no overlapping slots on one table.</summary>
public sealed class Reservation : AuditableEntity<Guid>, IBranchScoped
{
    public static readonly IReadOnlyList<ReservationStatus> ActiveStatuses =
        [ReservationStatus.Pending, ReservationStatus.Confirmed, ReservationStatus.Seated];

    private Reservation()
    {
    }

    public Reservation(
        Guid branchId, Guid? tableId, Guid? customerUserId, string customerName, string customerPhone, int partySize,
        DateTime reservedAtUtc, int durationMinutes, string? note, ReservationStatus initialStatus)
    {
        Id = Guid.NewGuid();
        BranchId = branchId;
        TableId = tableId;
        CustomerUserId = customerUserId;
        CustomerName = customerName.Trim();
        CustomerPhone = customerPhone.Trim();
        PartySize = partySize;
        ReservedAtUtc = reservedAtUtc;
        DurationMinutes = durationMinutes;
        EndsAtUtc = reservedAtUtc.AddMinutes(durationMinutes);
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        Status = initialStatus;
    }

    public Guid BranchId { get; private set; }

    public Guid? TableId { get; private set; }

    /// <summary>User id (Identity) of the customer who booked online; null for phone / walk-in bookings by staff.</summary>
    public Guid? CustomerUserId { get; private set; }

    public string CustomerName { get; private set; } = string.Empty;

    public string CustomerPhone { get; private set; } = string.Empty;

    public int PartySize { get; private set; }

    public DateTime ReservedAtUtc { get; private set; }

    public int DurationMinutes { get; private set; }

    /// <summary>Stored (not computed) so overlap checks can run in SQL.</summary>
    public DateTime EndsAtUtc { get; private set; }

    public ReservationStatus Status { get; private set; }

    public string? Note { get; private set; }

    public string? CancelReason { get; private set; }

    public void Confirm(Guid tableId)
    {
        Ensure([ReservationStatus.Pending], "confirmed");
        TableId = tableId;
        Status = ReservationStatus.Confirmed;
    }

    public void Seat() => MoveTo(ReservationStatus.Seated, [ReservationStatus.Confirmed], "seated");

    public void Complete() => MoveTo(ReservationStatus.Completed, [ReservationStatus.Seated], "completed");

    public void MarkNoShow() => MoveTo(ReservationStatus.NoShow, [ReservationStatus.Pending, ReservationStatus.Confirmed], "marked as no-show");

    public void Cancel(string? reason)
    {
        MoveTo(ReservationStatus.Cancelled, [ReservationStatus.Pending, ReservationStatus.Confirmed], "cancelled");
        CancelReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
    }

    private void MoveTo(ReservationStatus target, ReservationStatus[] allowedFrom, string action)
    {
        Ensure(allowedFrom, action);
        Status = target;
    }

    private void Ensure(ReservationStatus[] allowedFrom, string action)
    {
        if (!allowedFrom.Contains(Status))
        {
            throw new BusinessRuleException("reservation.invalid_status", $"A reservation in status {Status} cannot be {action}.");
        }
    }
}
