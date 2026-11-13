using AICOS.BuildingBlocks.Domain;
using AICOS.BuildingBlocks.Exceptions;

namespace AICOS.WorkforceScheduling.Api.Domain;

/// <summary>BR-53: full-time staff are paid by the working day, part-time staff by the working hour.</summary>
public enum ContractType
{
    FullTime,
    PartTime,
}

public enum EmployeeStatus
{
    Active,
    Inactive,
}

/// <summary>
/// HR profile of a Barista or Cashier in one branch (UC-27). <see cref="UserId"/> is the login account in Identity Service
/// (no FK across services); a token sub claim finds the employee for check-in and "my shifts". The contract type decides
/// how pay is calculated; the rates come from the brand pay rules (UC-19).
/// </summary>
public sealed class Employee : AuditableEntity<Guid>, IBranchScoped
{
    private Employee()
    {
    }

    public Employee(Guid tenantId, Guid branchId, Guid userId, string fullName, string role, string? position, DateOnly? hireDate, ContractType contractType)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        BranchId = branchId;
        UserId = userId;
        Role = role;
        Status = EmployeeStatus.Active;
        Update(fullName, position, hireDate, contractType);
    }

    public Guid TenantId { get; private set; }

    public Guid BranchId { get; private set; }

    public Guid UserId { get; private set; }

    public string FullName { get; private set; } = string.Empty;

    /// <summary>The role of the login account when the profile was created: Barista or Cashier.</summary>
    public string Role { get; private set; } = string.Empty;

    public string? Position { get; private set; }

    public DateOnly? HireDate { get; private set; }

    public ContractType ContractType { get; private set; }

    public EmployeeStatus Status { get; private set; }

    public void Update(string fullName, string? position, DateOnly? hireDate, ContractType contractType)
    {
        FullName = fullName.Trim();
        Position = string.IsNullOrWhiteSpace(position) ? null : position.Trim();
        HireDate = hireDate;
        ContractType = contractType;
    }

    public void ChangeStatus(EmployeeStatus status) => Status = status;

    public const int BankNameMaxLength = 50;
    public const int BankAccountNumberMaxLength = 30;
    public const int BankAccountNameMaxLength = 100;

    /// <summary>Where the salary is transferred: the bank (short name or code, e.g. "VCB"), the account number and the name on the account.</summary>
    public string? BankName { get; private set; }

    public string? BankAccountNumber { get; private set; }

    public string? BankAccountName { get; private set; }

    public bool HasBankAccount => BankName is not null && BankAccountNumber is not null && BankAccountName is not null;

    /// <summary>Sets the salary account, or clears it when every part is null.</summary>
    public void SetBankAccount(string? bankName, string? accountNumber, string? accountName)
    {
        BankName = string.IsNullOrWhiteSpace(bankName) ? null : bankName.Trim();
        BankAccountNumber = string.IsNullOrWhiteSpace(accountNumber) ? null : accountNumber.Trim();
        BankAccountName = string.IsNullOrWhiteSpace(accountName) ? null : accountName.Trim().ToUpperInvariant();
    }
}

/// <summary>Shift template of a branch, e.g. "Ca sáng" 06:00-12:00. No overnight shifts: end is after start.</summary>
public sealed class WorkShift : AuditableEntity<Guid>, IBranchScoped
{
    private WorkShift()
    {
    }

    public WorkShift(Guid tenantId, Guid branchId, string name, TimeOnly startTime, TimeOnly endTime)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        BranchId = branchId;
        IsActive = true;
        Update(name, startTime, endTime);
    }

    public Guid TenantId { get; private set; }

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

    /// <summary>Set when the manager deleted a template that past shifts still point to: it is hidden but kept for attendance and payroll.</summary>
    public DateTime? DeletedAtUtc { get; private set; }

    public void MarkDeleted(DateTime now)
    {
        IsActive = false;
        DeletedAtUtc = now;
    }
}

public enum AssignmentStatus
{
    /// <summary>Manager assigned it; waiting for the employee to confirm.</summary>
    Assigned,
    Confirmed,
    Declined,

    /// <summary>An approved replacement employee works this shift instead (UC-30). The original is not paid for it.</summary>
    Replaced,
}

public enum AttendanceReviewStatus
{
    /// <summary>Recorded by the employee, not yet reviewed by the Branch Manager. Not used for payroll (BR-54).</summary>
    Unreviewed,

    /// <summary>The Branch Manager approved it (as recorded, corrected, or as an absence). Payroll uses it.</summary>
    Approved,
}

/// <summary>
/// One employee working one shift on one date. Shift times are copied at assignment time so later edits of the template do
/// not rewrite history. Check-in/out is recorded here (UC-48); the Branch Manager reviews it (UC-29) before payroll uses it.
/// </summary>
public sealed class ShiftAssignment : AuditableEntity<Guid>, IBranchScoped
{
    public static readonly IReadOnlyList<AssignmentStatus> ActiveStatuses = [AssignmentStatus.Assigned, AssignmentStatus.Confirmed];

    private ShiftAssignment()
    {
    }

    public ShiftAssignment(Guid tenantId, Guid branchId, Guid employeeId, WorkShift shift, DateOnly workDate)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        BranchId = branchId;
        EmployeeId = employeeId;
        WorkShiftId = shift.Id;
        WorkDate = workDate;
        StartTime = shift.StartTime;
        EndTime = shift.EndTime;
        Status = AssignmentStatus.Assigned;
    }

    /// <summary>The shift of an approved replacement: the volunteer already agreed, so it starts confirmed.</summary>
    public ShiftAssignment(Guid tenantId, Guid branchId, Guid employeeId, Guid? workShiftId, DateOnly workDate, TimeOnly start, TimeOnly end, Guid replacementShiftId)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        BranchId = branchId;
        EmployeeId = employeeId;
        WorkShiftId = workShiftId;
        WorkDate = workDate;
        StartTime = start;
        EndTime = end;
        Status = AssignmentStatus.Confirmed;
        IsReplacement = true;
        ReplacementShiftId = replacementShiftId;
    }

    public Guid TenantId { get; private set; }

    public Guid BranchId { get; private set; }

    public Guid EmployeeId { get; private set; }

    /// <summary>Null for a replacement shift that was opened without a template.</summary>
    public Guid? WorkShiftId { get; private set; }

    public DateOnly WorkDate { get; private set; }

    public TimeOnly StartTime { get; private set; }

    public TimeOnly EndTime { get; private set; }

    public AssignmentStatus Status { get; private set; }

    public string? DeclineReason { get; private set; }

    /// <summary>BR-52: working a replacement shift never changes the employment type; it is only paid at the replacement rate.</summary>
    public bool IsReplacement { get; private set; }

    public Guid? ReplacementShiftId { get; private set; }

    public DateTime? CheckInAtUtc { get; private set; }

    public DateTime? CheckOutAtUtc { get; private set; }

    public AttendanceReviewStatus ReviewStatus { get; private set; }

    /// <summary>The Branch Manager recorded that the employee did not work this shift. Approved, with zero hours.</summary>
    public bool IsAbsent { get; private set; }

    public DateTime? AdjustedCheckInAtUtc { get; private set; }

    public DateTime? AdjustedCheckOutAtUtc { get; private set; }

    public string? ReviewNote { get; private set; }

    public Guid? ReviewedBy { get; private set; }

    public DateTime? ReviewedAtUtc { get; private set; }

    public double Hours => (EndTime - StartTime).TotalHours;

    public DateTime? EffectiveCheckInUtc => AdjustedCheckInAtUtc ?? CheckInAtUtc;

    public DateTime? EffectiveCheckOutUtc => AdjustedCheckOutAtUtc ?? CheckOutAtUtc;

    /// <summary>Hours actually worked according to the effective attendance times. Zero for absences and unfinished records.</summary>
    public double WorkedHours =>
        IsAbsent || EffectiveCheckInUtc is null || EffectiveCheckOutUtc is null
            ? 0
            : Math.Round(Math.Max(0, (EffectiveCheckOutUtc.Value - EffectiveCheckInUtc.Value).TotalHours), 2);

    /// <summary>Only an approved record of a shift that was really assigned counts for payroll (BR-54).</summary>
    public bool CountsForPayroll => ReviewStatus == AttendanceReviewStatus.Approved && Status is AssignmentStatus.Assigned or AssignmentStatus.Confirmed;

    public bool Overlaps(TimeOnly start, TimeOnly end) => StartTime < end && start < EndTime;

    public void Confirm()
    {
        EnsureStatus(AssignmentStatus.Assigned, "confirmed");
        Status = AssignmentStatus.Confirmed;
    }

    public void Decline(string reason)
    {
        if (Status == AssignmentStatus.Declined || Status == AssignmentStatus.Replaced || CheckInAtUtc is not null)
        {
            throw new BusinessRuleException("shift.invalid_status", "This shift can no longer be declined.");
        }

        Status = AssignmentStatus.Declined;
        DeclineReason = reason.Trim();
    }

    public void MarkReplaced() => Status = AssignmentStatus.Replaced;

    public void CheckIn(DateTime nowUtc) => CheckInAtUtc = nowUtc;

    public void CheckOut(DateTime nowUtc) => CheckOutAtUtc = nowUtc;

    /// <summary>Approve the attendance as recorded. It needs both times (or an absence): otherwise it is corrected instead.</summary>
    public void ApproveAsRecorded(Guid? managerId, string? note, DateTime nowUtc)
    {
        if (!IsAbsent && (EffectiveCheckInUtc is null || EffectiveCheckOutUtc is null))
        {
            throw new BusinessRuleException(
                "attendance.incomplete", "The record has no check-in or check-out. Correct the times or mark the employee absent first.");
        }

        Review(managerId, note, nowUtc);
    }

    /// <summary>Correct the times (a forgotten check-out, a wrong tap) and approve the corrected record in one step.</summary>
    public void Adjust(DateTime? checkInUtc, DateTime? checkOutUtc, string reason, Guid? managerId, DateTime nowUtc)
    {
        AdjustedCheckInAtUtc = checkInUtc ?? AdjustedCheckInAtUtc;
        AdjustedCheckOutAtUtc = checkOutUtc ?? AdjustedCheckOutAtUtc;
        IsAbsent = false;

        if (EffectiveCheckInUtc is null || EffectiveCheckOutUtc is null || EffectiveCheckOutUtc <= EffectiveCheckInUtc)
        {
            throw new BusinessRuleException("attendance.invalid_times", "After the correction the record needs a check-in and a later check-out.");
        }

        Review(managerId, reason, nowUtc);
    }

    public void MarkAbsent(string reason, Guid? managerId, DateTime nowUtc)
    {
        IsAbsent = true;
        AdjustedCheckInAtUtc = null;
        AdjustedCheckOutAtUtc = null;
        Review(managerId, reason, nowUtc);
    }

    private void Review(Guid? managerId, string? note, DateTime nowUtc)
    {
        ReviewStatus = AttendanceReviewStatus.Approved;
        ReviewNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        ReviewedBy = managerId;
        ReviewedAtUtc = nowUtc;
    }

    private void EnsureStatus(AssignmentStatus expected, string action)
    {
        if (Status != expected)
        {
            throw new BusinessRuleException("shift.invalid_status", $"A shift in status {Status} cannot be {action}.");
        }
    }
}

public enum ReplacementStatus
{
    /// <summary>Waiting for volunteers and for the Branch Manager to pick one.</summary>
    Open,

    /// <summary>A replacement was approved; the shift has an assignment now.</summary>
    Filled,

    Cancelled,
}

public enum ApplicationStatus
{
    Pending,
    Approved,
    Rejected,
    Withdrawn,
}

/// <summary>
/// A shift that needs somebody to work it instead of the person it was assigned to (UC-30). The Branch Manager opens it, staff of
/// the branch register (UC-31), and the replacement only takes effect when the Branch Manager approves it (BR-51).
/// </summary>
public sealed class ReplacementShift : AuditableEntity<Guid>, IBranchScoped
{
    private ReplacementShift()
    {
    }

    public ReplacementShift(
        Guid tenantId, Guid branchId, Guid? workShiftId, DateOnly workDate, TimeOnly start, TimeOnly end, Guid? originalAssignmentId, string reason)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        BranchId = branchId;
        WorkShiftId = workShiftId;
        WorkDate = workDate;
        StartTime = start;
        EndTime = end;
        OriginalAssignmentId = originalAssignmentId;
        Reason = reason.Trim();
        Status = ReplacementStatus.Open;
    }

    public Guid TenantId { get; private set; }

    public Guid BranchId { get; private set; }

    public Guid? WorkShiftId { get; private set; }

    public DateOnly WorkDate { get; private set; }

    public TimeOnly StartTime { get; private set; }

    public TimeOnly EndTime { get; private set; }

    /// <summary>The assignment being replaced, when the shift was opened from one. Null for an extra shift nobody was assigned to.</summary>
    public Guid? OriginalAssignmentId { get; private set; }

    public string Reason { get; private set; } = string.Empty;

    public ReplacementStatus Status { get; private set; }

    public Guid? ApprovedEmployeeId { get; private set; }

    public Guid? ApprovedAssignmentId { get; private set; }

    public string? ManagerNote { get; private set; }

    public Guid? DecidedBy { get; private set; }

    public DateTime? DecidedAtUtc { get; private set; }

    public double Hours => (EndTime - StartTime).TotalHours;

    public void Fill(Guid employeeId, Guid assignmentId, string? note, Guid? managerId, DateTime nowUtc)
    {
        EnsureOpen();
        Status = ReplacementStatus.Filled;
        ApprovedEmployeeId = employeeId;
        ApprovedAssignmentId = assignmentId;
        Decide(note, managerId, nowUtc);
    }

    public void Cancel(string? note, Guid? managerId, DateTime nowUtc)
    {
        EnsureOpen();
        Status = ReplacementStatus.Cancelled;
        Decide(note, managerId, nowUtc);
    }

    private void Decide(string? note, Guid? managerId, DateTime nowUtc)
    {
        ManagerNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        DecidedBy = managerId;
        DecidedAtUtc = nowUtc;
    }

    private void EnsureOpen()
    {
        if (Status != ReplacementStatus.Open)
        {
            throw new BusinessRuleException("replacement.not_open", $"A replacement shift in status {Status} can no longer be changed.");
        }
    }
}

/// <summary>An employee volunteering to work a replacement shift (UC-31).</summary>
public sealed class ReplacementApplication : AuditableEntity<Guid>
{
    private ReplacementApplication()
    {
    }

    public ReplacementApplication(Guid replacementShiftId, Guid employeeId, string? note)
    {
        Id = Guid.NewGuid();
        ReplacementShiftId = replacementShiftId;
        EmployeeId = employeeId;
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        Status = ApplicationStatus.Pending;
    }

    public Guid ReplacementShiftId { get; private set; }

    public Guid EmployeeId { get; private set; }

    public string? Note { get; private set; }

    public ApplicationStatus Status { get; private set; }

    public void Approve() => Status = ApplicationStatus.Approved;

    public void Reject() => Status = ApplicationStatus.Rejected;

    public void Withdraw()
    {
        if (Status != ApplicationStatus.Pending)
        {
            throw new BusinessRuleException("replacement.application_closed", $"An application in status {Status} cannot be withdrawn.");
        }

        Status = ApplicationStatus.Withdrawn;
    }
}

/// <summary>
/// How a brand pays its staff (UC-19, FR-17, BR-53). One row per tenant. Full-time staff earn <see cref="FullTimeDayRate"/> per
/// working day, part-time staff <see cref="PartTimeHourRate"/> per hour, and every hour worked in an approved replacement shift
/// earns <see cref="ReplacementHourRate"/>. Hourly pay beyond <see cref="OvertimeAfterHours"/> in one day is multiplied by
/// <see cref="OvertimeMultiplier"/> (the multiplier is an assumption to be checked against current labour regulations).
/// </summary>
public sealed class PayRule : AuditableEntity<Guid>
{
    private PayRule()
    {
    }

    public PayRule(Guid tenantId, decimal fullTimeDayRate, decimal partTimeHourRate, decimal replacementHourRate, decimal overtimeAfterHours, decimal overtimeMultiplier)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        Update(fullTimeDayRate, partTimeHourRate, replacementHourRate, overtimeAfterHours, overtimeMultiplier);
    }

    public Guid TenantId { get; private set; }

    public decimal FullTimeDayRate { get; private set; }

    public decimal PartTimeHourRate { get; private set; }

    public decimal ReplacementHourRate { get; private set; }

    public decimal OvertimeAfterHours { get; private set; }

    public decimal OvertimeMultiplier { get; private set; }

    public void Update(decimal fullTimeDayRate, decimal partTimeHourRate, decimal replacementHourRate, decimal overtimeAfterHours, decimal overtimeMultiplier)
    {
        FullTimeDayRate = fullTimeDayRate;
        PartTimeHourRate = partTimeHourRate;
        ReplacementHourRate = replacementHourRate;
        OvertimeAfterHours = overtimeAfterHours;
        OvertimeMultiplier = overtimeMultiplier;
    }
}

public enum CashClosingStatus
{
    /// <summary>The cashier counted the drawer; the Branch Manager has not confirmed yet.</summary>
    PendingReview,

    /// <summary>The Branch Manager reviewed the difference and closed the shift (BR-55).</summary>
    Confirmed,
}

/// <summary>
/// End-of-shift cash reconciliation of one cashier shift (UC-46, UC-33). The system computes the expected amount from the cash
/// recorded during the shift; the cashier enters what was counted; the Branch Manager reviews any difference and confirms it (BR-55).
/// </summary>
public sealed class CashClosing : AuditableEntity<Guid>, IBranchScoped
{
    private CashClosing()
    {
    }

    public CashClosing(Guid tenantId, Guid branchId, Guid assignmentId, Guid employeeId, decimal expectedAmount, decimal countedAmount, string? note, DateTime nowUtc)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        BranchId = branchId;
        AssignmentId = assignmentId;
        EmployeeId = employeeId;
        ExpectedAmount = expectedAmount;
        CountedAmount = countedAmount;
        Difference = countedAmount - expectedAmount;
        CashierNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        SubmittedAtUtc = nowUtc;
        Status = CashClosingStatus.PendingReview;
    }

    public Guid TenantId { get; private set; }

    public Guid BranchId { get; private set; }

    public Guid AssignmentId { get; private set; }

    public Guid EmployeeId { get; private set; }

    public decimal ExpectedAmount { get; private set; }

    public decimal CountedAmount { get; private set; }

    /// <summary>Counted minus expected: negative when cash is missing, positive when there is more than expected.</summary>
    public decimal Difference { get; private set; }

    public string? CashierNote { get; private set; }

    public DateTime SubmittedAtUtc { get; private set; }

    public CashClosingStatus Status { get; private set; }

    public string? ManagerNote { get; private set; }

    public Guid? ConfirmedBy { get; private set; }

    public DateTime? ConfirmedAtUtc { get; private set; }

    public void Confirm(string? note, Guid? managerId, DateTime nowUtc)
    {
        if (Status == CashClosingStatus.Confirmed)
        {
            throw new BusinessRuleException("cash_closing.already_confirmed", "This cash closing was already confirmed.");
        }

        if (Difference != 0 && string.IsNullOrWhiteSpace(note))
        {
            throw new BusinessRuleException("cash_closing.note_required", "A note is required to confirm a closing with a cash difference (BR-55).");
        }

        Status = CashClosingStatus.Confirmed;
        ManagerNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        ConfirmedBy = managerId;
        ConfirmedAtUtc = nowUtc;
    }
}

/// <summary>
/// Cash taken or paid out by a cashier (a counter sale, a cash refund), recorded by the order and payment flows through
/// <c>CashPaymentRecordedEvent</c>. The expected amount of a cash closing is the sum of the entries of that cashier during the shift.
/// </summary>
public sealed class CashTransaction : Entity<Guid>
{
    private CashTransaction()
    {
    }

    public CashTransaction(Guid sourceEventId, Guid tenantId, Guid branchId, Guid cashierUserId, Guid? orderId, decimal amount, DateTime occurredAtUtc)
    {
        Id = Guid.NewGuid();
        SourceEventId = sourceEventId;
        TenantId = tenantId;
        BranchId = branchId;
        CashierUserId = cashierUserId;
        OrderId = orderId;
        Amount = amount;
        OccurredAtUtc = occurredAtUtc;
    }

    /// <summary>Makes handling a redelivered event idempotent.</summary>
    public Guid SourceEventId { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid BranchId { get; private set; }

    public Guid CashierUserId { get; private set; }

    public Guid? OrderId { get; private set; }

    /// <summary>Positive for cash received, negative for cash paid back.</summary>
    public decimal Amount { get; private set; }

    public DateTime OccurredAtUtc { get; private set; }
}

/// <summary>
/// A manual correction to someone's pay for a period: a bonus, a deduction, or a fix for a mistake (FR-67, BR-56: payroll adjustments are
/// audited). It is added to the employee's total in the payroll summary of the period its <see cref="WorkDate"/> falls in.
/// </summary>
public sealed class PayrollAdjustment : AuditableEntity<Guid>, IBranchScoped
{
    public const int ReasonMaxLength = 300;

    private PayrollAdjustment()
    {
    }

    public PayrollAdjustment(Guid tenantId, Guid branchId, Guid employeeId, DateOnly workDate, decimal amount, string reason)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        BranchId = branchId;
        EmployeeId = employeeId;
        WorkDate = workDate;
        Amount = amount;
        Reason = reason;
    }

    public Guid TenantId { get; private set; }

    public Guid BranchId { get; private set; }

    public Guid EmployeeId { get; private set; }

    /// <summary>The day the adjustment belongs to; it counts in the payroll period that covers this date.</summary>
    public DateOnly WorkDate { get; private set; }

    /// <summary>VND; positive adds to the pay, negative deducts.</summary>
    public decimal Amount { get; private set; }

    public string Reason { get; private set; } = string.Empty;
}
