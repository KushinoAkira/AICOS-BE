using AICOS.BuildingBlocks.Domain;
using AICOS.BuildingBlocks.Exceptions;

namespace AICOS.WorkforceScheduling.Api.Domain;

/// <summary>
/// Finalized: the figures are frozen and the attendance and adjustments of the period are locked. Paid: every line is paid (or had nothing
/// to pay). Reopened: the period was undone to correct it; it no longer locks anything and a new period can be finalized in its place.
/// </summary>
public enum PayrollPeriodStatus
{
    Finalized,
    Paid,
    Reopened,
}

public enum PayoutMethod
{
    BankTransfer,
    Cash,
}

/// <summary>
/// The payroll of one branch for a closed range of days, frozen when the manager finalizes it. The pay itself leaves the shop's own bank
/// account (or the till); AICOS records who was paid, when, how and with which bank reference.
/// </summary>
public sealed class PayrollPeriod : AuditableEntity<Guid>, IBranchScoped
{
    public const int ReasonMaxLength = 500;

    private readonly List<PayrollPeriodLine> _lines = [];

    private PayrollPeriod()
    {
    }

    public PayrollPeriod(Guid tenantId, Guid branchId, DateOnly from, DateOnly to, Guid? finalizedBy, DateTime now)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        BranchId = branchId;
        From = from;
        To = to;
        Status = PayrollPeriodStatus.Finalized;
        FinalizedBy = finalizedBy;
        FinalizedAtUtc = now;
    }

    public Guid TenantId { get; private set; }

    public Guid BranchId { get; private set; }

    public DateOnly From { get; private set; }

    public DateOnly To { get; private set; }

    public PayrollPeriodStatus Status { get; private set; }

    public Guid? FinalizedBy { get; private set; }

    public DateTime FinalizedAtUtc { get; private set; }

    public Guid? ReopenedBy { get; private set; }

    public DateTime? ReopenedAtUtc { get; private set; }

    public string? ReopenReason { get; private set; }

    public IReadOnlyList<PayrollPeriodLine> Lines => _lines;

    /// <summary>Whether the period still freezes its days (attendance and adjustments cannot change).</summary>
    public bool Locks => Status != PayrollPeriodStatus.Reopened;

    public decimal TotalNet => _lines.Sum(l => l.NetPay);

    public void AddLine(PayrollPeriodLine line)
    {
        _lines.Add(line);
        RefreshStatus();
    }

    /// <summary>Records that these employees were paid. All or nothing: one already paid, or one with nothing to pay, refuses the whole request.</summary>
    public void MarkPaid(IReadOnlyCollection<Guid> employeeIds, PayoutMethod method, string? reference, Guid? paidBy, DateTime now)
    {
        if (Status == PayrollPeriodStatus.Reopened)
        {
            throw new ConflictException("The payroll period was reopened; finalize it again before paying.", "payroll.period_reopened");
        }

        var lines = new List<PayrollPeriodLine>();
        foreach (var id in employeeIds.Distinct())
        {
            var line = _lines.FirstOrDefault(l => l.EmployeeId == id) ?? throw new NotFoundException("Payroll line of employee", id);
            if (line.IsPaid)
            {
                throw new ConflictException($"{line.EmployeeName} is already marked as paid for this period.", "payroll.already_paid");
            }

            if (line.NetPay <= 0)
            {
                throw new BusinessRuleException("payroll.nothing_to_pay", $"{line.EmployeeName} has nothing to be paid for this period.");
            }

            lines.Add(line);
        }

        foreach (var line in lines)
        {
            line.MarkPaid(method, reference, paidBy, now);
        }

        RefreshStatus();
    }

    /// <summary>Undoes the period so its days can be corrected. Refused once anyone was paid: that money has already left the shop.</summary>
    public void Reopen(string reason, Guid? by, DateTime now)
    {
        if (Status == PayrollPeriodStatus.Reopened)
        {
            throw new ConflictException("The payroll period is already reopened.", "payroll.period_reopened");
        }

        if (_lines.Any(l => l.IsPaid))
        {
            throw new ConflictException("Some employees of this period are already paid, so it cannot be reopened. Correct it in the next period with an adjustment.", "payroll.period_paid");
        }

        Status = PayrollPeriodStatus.Reopened;
        ReopenReason = reason;
        ReopenedBy = by;
        ReopenedAtUtc = now;
    }

    private void RefreshStatus()
    {
        if (Status != PayrollPeriodStatus.Reopened)
        {
            Status = _lines.All(l => l.IsSettled) ? PayrollPeriodStatus.Paid : PayrollPeriodStatus.Finalized;
        }
    }
}

/// <summary>
/// One employee's pay in a finalized period: the figures of the payroll summary at that moment, the salary account it goes to, and once paid,
/// how and when. <see cref="NetPay"/> is never negative: deductions larger than the pay leave nothing to pay (and a warning).
/// </summary>
public sealed class PayrollPeriodLine : Entity<Guid>
{
    public const int ReferenceMaxLength = 100;

    private PayrollPeriodLine()
    {
    }

    public PayrollPeriodLine(
        Guid periodId, Employee employee, int workedDays, double regularHours, double overtimeHours, double replacementHours,
        decimal basePay, decimal overtimePay, decimal replacementPay, decimal adjustments, decimal grossPay)
    {
        Id = Guid.NewGuid();
        PeriodId = periodId;
        EmployeeId = employee.Id;
        EmployeeName = employee.FullName;
        ContractType = employee.ContractType;
        WorkedDays = workedDays;
        RegularHours = regularHours;
        OvertimeHours = overtimeHours;
        ReplacementHours = replacementHours;
        BasePay = basePay;
        OvertimePay = overtimePay;
        ReplacementPay = replacementPay;
        Adjustments = adjustments;
        GrossPay = grossPay;
        NetPay = Math.Max(0, grossPay);
        BankName = employee.BankName;
        BankAccountNumber = employee.BankAccountNumber;
        BankAccountName = employee.BankAccountName;
    }

    public Guid PeriodId { get; private set; }

    public Guid EmployeeId { get; private set; }

    public string EmployeeName { get; private set; } = string.Empty;

    public ContractType ContractType { get; private set; }

    public int WorkedDays { get; private set; }

    public double RegularHours { get; private set; }

    public double OvertimeHours { get; private set; }

    public double ReplacementHours { get; private set; }

    public decimal BasePay { get; private set; }

    public decimal OvertimePay { get; private set; }

    public decimal ReplacementPay { get; private set; }

    public decimal Adjustments { get; private set; }

    /// <summary>Pay plus adjustments; negative when deductions were larger than the pay.</summary>
    public decimal GrossPay { get; private set; }

    /// <summary>What is transferred: <see cref="GrossPay"/>, but never below zero.</summary>
    public decimal NetPay { get; private set; }

    public string? BankName { get; private set; }

    public string? BankAccountNumber { get; private set; }

    public string? BankAccountName { get; private set; }

    public bool HasBankAccount => BankName is not null && BankAccountNumber is not null && BankAccountName is not null;

    public DateTime? PaidAtUtc { get; private set; }

    public Guid? PaidBy { get; private set; }

    public PayoutMethod? PayoutMethod { get; private set; }

    /// <summary>The bank transaction reference of the transfer, or the cash receipt number.</summary>
    public string? PayoutReference { get; private set; }

    public bool IsPaid => PaidAtUtc is not null;

    /// <summary>Nothing more to do: paid, or there was nothing to pay.</summary>
    public bool IsSettled => IsPaid || NetPay <= 0;

    internal void MarkPaid(PayoutMethod method, string? reference, Guid? by, DateTime now)
    {
        PayoutMethod = method;
        PayoutReference = reference;
        PaidBy = by;
        PaidAtUtc = now;
    }
}
