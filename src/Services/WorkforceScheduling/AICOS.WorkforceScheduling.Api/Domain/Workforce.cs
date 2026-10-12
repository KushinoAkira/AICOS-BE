using AICOS.BuildingBlocks.Domain;
using AICOS.BuildingBlocks.Exceptions;

namespace AICOS.WorkforceScheduling.Api.Domain;

public enum PaymentType
{
    Hourly,
    Fixed,
}

public enum EmployeeStatus
{
    Active,
    Inactive,
}

/// <summary>
/// HR profile of a staff member in one branch. <see cref="UserId"/> is the login account in Identity Service
/// (no FK across services); a token's "sub" claim finds the employee for check-in and "my shifts".
/// </summary>
public sealed class Employee : AuditableEntity<Guid>, IBranchScoped
{
    private Employee()
    {
    }

    public Employee(Guid branchId, Guid userId, string fullName, string? position, DateOnly? hireDate, PaymentType paymentType, decimal? hourlyRate, decimal? fixedSalary)
    {
        Id = Guid.NewGuid();
        BranchId = branchId;
        UserId = userId;
        Status = EmployeeStatus.Active;
        Update(fullName, position, hireDate, paymentType, hourlyRate, fixedSalary);
    }

    public Guid BranchId { get; private set; }

    public Guid UserId { get; private set; }

    public string FullName { get; private set; } = string.Empty;

    public string? Position { get; private set; }

    public DateOnly? HireDate { get; private set; }

    public PaymentType PaymentType { get; private set; }

    public decimal? HourlyRate { get; private set; }

    public decimal? FixedSalary { get; private set; }

    public EmployeeStatus Status { get; private set; }

    public void Update(string fullName, string? position, DateOnly? hireDate, PaymentType paymentType, decimal? hourlyRate, decimal? fixedSalary)
    {
        FullName = fullName.Trim();
        Position = string.IsNullOrWhiteSpace(position) ? null : position.Trim();
        HireDate = hireDate;
        PaymentType = paymentType;
        HourlyRate = paymentType == PaymentType.Hourly ? hourlyRate : null;
        FixedSalary = paymentType == PaymentType.Fixed ? fixedSalary : null;
    }

    public void ChangeStatus(EmployeeStatus status) => Status = status;
}

/// <summary>Shift template of a branch (UC-10), e.g. "Ca sáng" 06:00-12:00. No overnight shifts: end is after start.</summary>
public sealed class WorkShift : AuditableEntity<Guid>, IBranchScoped
{
    private WorkShift()
    {
    }

    public WorkShift(Guid branchId, string name, TimeOnly startTime, TimeOnly endTime)
    {
        Id = Guid.NewGuid();
        BranchId = branchId;
        IsActive = true;
        Update(name, startTime, endTime);
    }

    public Guid BranchId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public TimeOnly StartTime { get; private set; }

    public TimeOnly EndTime { get; private set; }

    public bool IsActive { get; private set; }

    public double Hours => (EndTime - StartTime).TotalHours;

    public void Update(string name, TimeOnly startTime, TimeOnly endTime)
    {
        Name = name.Trim();
        StartTime = startTime;
        EndTime = endTime;
    }

    public void SetActive(bool isActive) => IsActive = isActive;
}

public enum AssignmentStatus
{
    /// <summary>Manager assigned it; waiting for the employee to confirm.</summary>
    Assigned,
    Confirmed,
    Declined,
}

/// <summary>
/// One employee working one shift on one date (employee_shifts). Shift times are copied at assignment time so later
/// edits of the template do not rewrite history. Check-in/out (UC-42) is recorded here.
/// </summary>
public sealed class ShiftAssignment : AuditableEntity<Guid>, IBranchScoped
{
    public static readonly IReadOnlyList<AssignmentStatus> ActiveStatuses = [AssignmentStatus.Assigned, AssignmentStatus.Confirmed];

    private ShiftAssignment()
    {
    }

    public ShiftAssignment(Guid branchId, Guid employeeId, WorkShift shift, DateOnly workDate)
    {
        Id = Guid.NewGuid();
        BranchId = branchId;
        EmployeeId = employeeId;
        WorkShiftId = shift.Id;
        WorkDate = workDate;
        StartTime = shift.StartTime;
        EndTime = shift.EndTime;
        Status = AssignmentStatus.Assigned;
    }

    public Guid BranchId { get; private set; }

    public Guid EmployeeId { get; private set; }

    public Guid WorkShiftId { get; private set; }

    public DateOnly WorkDate { get; private set; }

    public TimeOnly StartTime { get; private set; }

    public TimeOnly EndTime { get; private set; }

    public AssignmentStatus Status { get; private set; }

    public string? DeclineReason { get; private set; }

    public DateTime? CheckInAtUtc { get; private set; }

    public DateTime? CheckOutAtUtc { get; private set; }

    public double Hours => (EndTime - StartTime).TotalHours;

    public bool Overlaps(TimeOnly start, TimeOnly end) => StartTime < end && start < EndTime;

    public void Confirm()
    {
        EnsureStatus(AssignmentStatus.Assigned, "confirmed");
        Status = AssignmentStatus.Confirmed;
    }

    public void Decline(string reason)
    {
        if (Status == AssignmentStatus.Declined || CheckInAtUtc is not null)
        {
            throw new BusinessRuleException("shift.invalid_status", "This shift can no longer be declined.");
        }

        Status = AssignmentStatus.Declined;
        DeclineReason = reason.Trim();
    }

    /// <summary>Approved shift swap (BR-23): the shift goes to another employee, who must confirm it again.</summary>
    public void Reassign(Guid employeeId)
    {
        EmployeeId = employeeId;
        Status = AssignmentStatus.Assigned;
    }

    public void CheckIn(DateTime nowUtc) => CheckInAtUtc = nowUtc;

    public void CheckOut(DateTime nowUtc) => CheckOutAtUtc = nowUtc;

    private void EnsureStatus(AssignmentStatus expected, string action)
    {
        if (Status != expected)
        {
            throw new BusinessRuleException("shift.invalid_status", $"A shift in status {Status} cannot be {action}.");
        }
    }
}

public enum SwapStatus
{
    Pending,
    Approved,
    Rejected,
    Cancelled,
}

/// <summary>Employee asks to give one of their shifts to a colleague (UC-32). BR-23: effective only after Manager approval (UC-11).</summary>
public sealed class ShiftSwapRequest : AuditableEntity<Guid>, IBranchScoped
{
    private ShiftSwapRequest()
    {
    }

    public ShiftSwapRequest(Guid branchId, Guid assignmentId, Guid requesterEmployeeId, Guid targetEmployeeId, string reason)
    {
        Id = Guid.NewGuid();
        BranchId = branchId;
        AssignmentId = assignmentId;
        RequesterEmployeeId = requesterEmployeeId;
        TargetEmployeeId = targetEmployeeId;
        Reason = reason.Trim();
        Status = SwapStatus.Pending;
    }

    public Guid BranchId { get; private set; }

    public Guid AssignmentId { get; private set; }

    public Guid RequesterEmployeeId { get; private set; }

    public Guid TargetEmployeeId { get; private set; }

    public string Reason { get; private set; } = string.Empty;

    public SwapStatus Status { get; private set; }

    public string? ManagerNote { get; private set; }

    public Guid? DecidedBy { get; private set; }

    public DateTime? DecidedAtUtc { get; private set; }

    public void Approve(Guid? managerId, string? note, DateTime nowUtc) => Decide(SwapStatus.Approved, managerId, note, nowUtc);

    public void Reject(Guid? managerId, string? note, DateTime nowUtc) => Decide(SwapStatus.Rejected, managerId, note, nowUtc);

    public void Cancel()
    {
        EnsurePending();
        Status = SwapStatus.Cancelled;
    }

    private void Decide(SwapStatus status, Guid? managerId, string? note, DateTime nowUtc)
    {
        EnsurePending();
        Status = status;
        ManagerNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        DecidedBy = managerId;
        DecidedAtUtc = nowUtc;
    }

    private void EnsurePending()
    {
        if (Status != SwapStatus.Pending)
        {
            throw new BusinessRuleException("swap.invalid_status", $"A swap request in status {Status} can no longer be changed.");
        }
    }
}
