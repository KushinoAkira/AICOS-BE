using System.Globalization;
using System.Text;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Security;
using AICOS.BuildingBlocks.Validation;
using AICOS.WorkforceScheduling.Api.Domain;
using AICOS.WorkforceScheduling.Api.Features.Employees;
using AICOS.WorkforceScheduling.Api.Infrastructure;
using AICOS.WorkforceScheduling.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.WorkforceScheduling.Api.Features.Payroll;

/// <summary>POST /payroll/periods: finalize the payroll of a branch for days that are over. <c>BranchId</c> is only read for a BrandAdmin.</summary>
public sealed record FinalizePayrollRequest(Guid? BranchId, DateOnly? From, DateOnly? To);

/// <summary>POST /payroll/periods/{id}/payments: these employees were paid. <c>Reference</c> is the bank transaction reference (required for a transfer).</summary>
public sealed record RecordPayoutRequest(List<Guid>? EmployeeIds, PayoutMethod? Method, string? Reference);

public sealed record ReopenPayrollRequest(string? Reason);

/// <summary>PUT /employees/{id}/bank-account and /employees/me/bank-account. All three parts, or all null to clear it.</summary>
public sealed record BankAccountRequest(string? BankName, string? AccountNumber, string? AccountName);

/// <summary>Something to sort out before or while paying: <c>NoBankAccount</c> (cannot be transferred) or <c>DeductionsExceedPay</c> (nothing to pay).</summary>
public sealed record PayrollWarning(Guid EmployeeId, string EmployeeName, string Code, string Message);

public sealed record PayrollPeriodLineResponse(
    Guid EmployeeId, string EmployeeName, ContractType ContractType, int WorkedDays, double RegularHours, double OvertimeHours, double ReplacementHours,
    decimal BasePay, decimal OvertimePay, decimal ReplacementPay, decimal Adjustments, decimal GrossPay, decimal NetPay,
    string? BankName, string? BankAccountNumber, string? BankAccountName, bool IsPaid, DateTime? PaidAtUtc, PayoutMethod? PayoutMethod, string? PayoutReference)
{
    public static PayrollPeriodLineResponse From(PayrollPeriodLine l) => new(
        l.EmployeeId, l.EmployeeName, l.ContractType, l.WorkedDays, l.RegularHours, l.OvertimeHours, l.ReplacementHours, l.BasePay, l.OvertimePay, l.ReplacementPay,
        l.Adjustments, l.GrossPay, l.NetPay, l.BankName, l.BankAccountNumber, l.BankAccountName, l.IsPaid, l.PaidAtUtc, l.PayoutMethod, l.PayoutReference);
}

public sealed record PayrollPeriodResponse(
    Guid Id, Guid BranchId, DateOnly From, DateOnly To, PayrollPeriodStatus Status, DateTime FinalizedAtUtc, Guid? FinalizedBy, DateTime? ReopenedAtUtc,
    string? ReopenReason, decimal TotalNet, decimal TotalPaid, int Employees, int Paid, IReadOnlyList<PayrollPeriodLineResponse>? Lines, IReadOnlyList<PayrollWarning> Warnings)
{
    public static PayrollPeriodResponse Of(PayrollPeriod p, bool withLines = true) => new(
        p.Id, p.BranchId, p.From, p.To, p.Status, p.FinalizedAtUtc, p.FinalizedBy, p.ReopenedAtUtc, p.ReopenReason, p.TotalNet,
        p.Lines.Where(l => l.IsPaid).Sum(l => l.NetPay), p.Lines.Count, p.Lines.Count(l => l.IsPaid),
        withLines ? p.Lines.OrderBy(l => l.EmployeeName).Select(PayrollPeriodLineResponse.From).ToList() : null,
        p.Status == PayrollPeriodStatus.Reopened ? [] : WarningsOf(p));

    private static List<PayrollWarning> WarningsOf(PayrollPeriod p)
    {
        var warnings = new List<PayrollWarning>();
        foreach (var l in p.Lines.OrderBy(l => l.EmployeeName))
        {
            if (l.GrossPay < 0)
            {
                warnings.Add(new PayrollWarning(l.EmployeeId, l.EmployeeName, "DeductionsExceedPay", $"Deductions are {-l.GrossPay:N0} VND more than the pay: nothing is paid."));
            }
            else if (!l.IsPaid && l.NetPay > 0 && !l.HasBankAccount)
            {
                warnings.Add(new PayrollWarning(l.EmployeeId, l.EmployeeName, "NoBankAccount", "No salary account: pay in cash or add the account and finalize again."));
            }
        }

        return warnings;
    }
}

/// <summary>An employee's own pay for one finalized period.</summary>
public sealed record PayslipResponse(
    Guid PeriodId, Guid BranchId, DateOnly From, DateOnly To, PayrollPeriodStatus PeriodStatus, int WorkedDays, double RegularHours, double OvertimeHours,
    double ReplacementHours, decimal BasePay, decimal OvertimePay, decimal ReplacementPay, decimal Adjustments, decimal GrossPay, decimal NetPay,
    string? BankName, string? BankAccountHint, bool IsPaid, DateTime? PaidAtUtc, PayoutMethod? PayoutMethod, string? PayoutReference);

/// <summary>
/// Paying staff after the payroll summary: finalize a period (the figures freeze and its days lock), record who was paid and how, reopen a
/// period that has to be corrected (only while nobody is paid), export the bank transfer list, and let each employee read their payslips.
/// Routes under /api/v1/workforce/payroll. Management: BranchManager for their branch, BrandAdmin for any branch of the brand.
/// </summary>
public static class PayrollPeriodEndpoints
{
    public static void MapPayrollPeriodEndpoints(this RouteGroupBuilder api)
    {
        var periods = api.MapGroup("/payroll/periods").WithTags("Payroll periods");
        periods.MapPost("/", FinalizeAsync).RequireAuthorization(AicosPolicies.Management);
        periods.MapGet("/", ListAsync).RequireAuthorization(AicosPolicies.Management);
        periods.MapGet("/{id:guid}", GetAsync).RequireAuthorization(AicosPolicies.Management);
        periods.MapPost("/{id:guid}/payments", RecordPayoutAsync).RequireAuthorization(AicosPolicies.Management);
        periods.MapPost("/{id:guid}/reopen", ReopenAsync).RequireAuthorization(AicosPolicies.Management);
        periods.MapGet("/{id:guid}/bank-transfer", BankTransferAsync).RequireAuthorization(AicosPolicies.Management);

        api.MapGet("/payroll/payslips/mine", MyPayslipsAsync).WithTags("Payroll periods").RequireAuthorization(AicosPolicies.Staff);

        var accounts = api.MapGroup("/employees").WithTags("Employees");
        accounts.MapPut("/me/bank-account", SetMyBankAccountAsync).RequireAuthorization(AicosPolicies.Staff);
        accounts.MapPut("/{id:guid}/bank-account", SetBankAccountAsync).RequireAuthorization(AicosPolicies.Management);
    }

    // ------------------------------------------------------------------ finalize
    private static async Task<IResult> FinalizeAsync(
        FinalizePayrollRequest r, WorkforceSchedulingDbContext db, ICurrentUser user, IBranchDirectory directory, ShopClock shop, IEventBus bus, ILoggerFactory loggers,
        CancellationToken ct)
    {
        var errors = new ValidationErrors();
        if (r.From is null || r.To is null)
        {
            errors.Add("From", "Both from and to dates are required (yyyy-MM-dd).");
        }
        else if (r.From > r.To || r.To.Value.DayNumber - r.From.Value.DayNumber >= PayrollEndpoints.MaxPeriodDays)
        {
            errors.Add("From", $"The period must be ordered and at most {PayrollEndpoints.MaxPeriodDays} days.");
        }

        errors.ThrowIfAny();

        if (r.To >= shop.Today)
        {
            throw new BusinessRuleException("payroll.period_not_over", "Only days that are over can be finalized: the period must end before today.");
        }

        var tenantId = TenantScope.Require(user);
        var branch = await BranchScope.ResolveAsync(user, directory, r.BranchId, requireActive: false, ct);
        if (await db.PayrollPeriods.AnyAsync(p => p.TenantId == tenantId && p.BranchId == branch && p.Status != PayrollPeriodStatus.Reopened && p.From <= r.To && p.To >= r.From, ct))
        {
            throw new ConflictException("Part of these days is already in a finalized payroll period of this branch.", "payroll.period_overlap");
        }

        var summary = await PayrollEndpoints.BuildAsync(db, user, directory, shop, branch, r.From, r.To, ct);
        if (summary.Totals.UnreviewedRecords > 0)
        {
            throw new BusinessRuleException(
                "payroll.unreviewed_records", $"{summary.Totals.UnreviewedRecords} shift(s) of the period are not reviewed yet. Approve, correct or mark them absent first.");
        }

        var ids = summary.Lines.Select(l => l.EmployeeId).ToList();
        var employees = await db.Employees.AsNoTracking().Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct);
        var period = new PayrollPeriod(tenantId, branch, r.From!.Value, r.To!.Value, user.UserId, shop.UtcNow);
        foreach (var l in summary.Lines)
        {
            period.AddLine(new PayrollPeriodLine(
                period.Id, employees[l.EmployeeId], l.WorkedDays, l.RegularHours, l.OvertimeHours, l.ReplacementHours, l.BasePay, l.OvertimePay, l.ReplacementPay,
                l.Adjustments, l.TotalPay));
        }

        db.PayrollPeriods.Add(period);
        await db.SaveChangesAsync(ct);
        await Audit(bus, user, loggers, tenantId, "payroll_period.finalized", period, null);
        return Results.Created($"/api/v1/workforce/payroll/periods/{period.Id}", PayrollPeriodResponse.Of(period));
    }

    // ------------------------------------------------------------------ read
    private static async Task<List<PayrollPeriodResponse>> ListAsync(
        WorkforceSchedulingDbContext db, ICurrentUser user, IBranchDirectory directory, Guid? branchId, CancellationToken ct)
    {
        var tenantId = TenantScope.Require(user);
        var branch = await WorkforceScope.FilterAsync(user, directory, branchId, ct);
        var query = db.PayrollPeriods.AsNoTracking().Include(p => p.Lines).Where(p => p.TenantId == tenantId);
        if (branch is not null)
        {
            query = query.Where(p => p.BranchId == branch);
        }

        var rows = await query.OrderByDescending(p => p.From).ThenByDescending(p => p.FinalizedAtUtc).Take(100).ToListAsync(ct);
        return rows.Select(p => PayrollPeriodResponse.Of(p, withLines: false)).ToList();
    }

    private static async Task<PayrollPeriodResponse> GetAsync(Guid id, WorkforceSchedulingDbContext db, ICurrentUser user, CancellationToken ct) =>
        PayrollPeriodResponse.Of(await FindAsync(db, user, id, ct));

    // ------------------------------------------------------------------ pay and reopen
    private static async Task<PayrollPeriodResponse> RecordPayoutAsync(
        Guid id, RecordPayoutRequest r, WorkforceSchedulingDbContext db, ICurrentUser user, ShopClock shop, IEventBus bus, ILoggerFactory loggers, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        if (r.EmployeeIds is not { Count: > 0 })
        {
            errors.Add("EmployeeIds", "List at least one employee who was paid.");
        }

        CommonRules.RequiredEnum(errors, "Method", r.Method, "Payout method");
        CommonRules.MaxLength(errors, "Reference", r.Reference, "Reference", PayrollPeriodLine.ReferenceMaxLength);
        if (r.Method == PayoutMethod.BankTransfer && string.IsNullOrWhiteSpace(r.Reference))
        {
            errors.Add("Reference", "Give the bank transaction reference of the transfer.");
        }

        errors.ThrowIfAny();

        var period = await FindAsync(db, user, id, ct);
        var before = Snapshot(period);
        period.MarkPaid(r.EmployeeIds!, r.Method!.Value, string.IsNullOrWhiteSpace(r.Reference) ? null : r.Reference.Trim(), user.UserId, shop.UtcNow);
        await db.SaveChangesAsync(ct);
        await Audit(bus, user, loggers, period.TenantId, "payroll_period.paid", period, before);
        return PayrollPeriodResponse.Of(period);
    }

    private static async Task<PayrollPeriodResponse> ReopenAsync(
        Guid id, ReopenPayrollRequest r, WorkforceSchedulingDbContext db, ICurrentUser user, ShopClock shop, IEventBus bus, ILoggerFactory loggers, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        CommonRules.Required(errors, "Reason", r.Reason, "Reason", PayrollPeriod.ReasonMaxLength);
        errors.ThrowIfAny();

        var period = await FindAsync(db, user, id, ct);
        var before = Snapshot(period);
        period.Reopen(r.Reason!.Trim(), user.UserId, shop.UtcNow);
        await db.SaveChangesAsync(ct);
        await Audit(bus, user, loggers, period.TenantId, "payroll_period.reopened", period, before);
        return PayrollPeriodResponse.Of(period);
    }

    // ------------------------------------------------------------------ bank transfer list
    /// <summary>
    /// The transfers still to make, as a CSV the owner fills into internet banking (UTF-8 with a byte-order mark for Excel). Employees with no
    /// salary account or nothing to pay are left out; the period's warnings name them.
    /// </summary>
    private static async Task<IResult> BankTransferAsync(Guid id, WorkforceSchedulingDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var period = await FindAsync(db, user, id, ct);
        if (period.Status == PayrollPeriodStatus.Reopened)
        {
            throw new ConflictException("The payroll period was reopened; finalize it again first.", "payroll.period_reopened");
        }

        var content = string.Create(CultureInfo.InvariantCulture, $"Luong {period.From:dd/MM}-{period.To:dd/MM/yyyy}");
        var csv = new StringBuilder();
        csv.AppendLine("STT,Ten nguoi nhan,So tai khoan,Ngan hang,So tien,Noi dung");
        var n = 0;
        foreach (var l in period.Lines.Where(l => !l.IsPaid && l.NetPay > 0 && l.HasBankAccount).OrderBy(l => l.EmployeeName))
        {
            n++;
            csv.AppendLine(string.Join(',',
                n.ToString(CultureInfo.InvariantCulture), PayrollEndpoints.Escape(l.BankAccountName!), PayrollEndpoints.Escape(l.BankAccountNumber!), PayrollEndpoints.Escape(l.BankName!),
                l.NetPay.ToString("0", CultureInfo.InvariantCulture), PayrollEndpoints.Escape(content)));
        }

        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();
        return Results.File(bytes, "text/csv; charset=utf-8", $"chuyen-khoan-luong-{period.From:yyyyMMdd}-{period.To:yyyyMMdd}.csv");
    }

    // ------------------------------------------------------------------ employee side
    private static async Task<List<PayslipResponse>> MyPayslipsAsync(WorkforceSchedulingDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var me = await EmployeeLookup.CurrentAsync(db, user, ct);
        var rows = await (
                from l in db.PayrollPeriodLines.AsNoTracking()
                join p in db.PayrollPeriods.AsNoTracking() on l.PeriodId equals p.Id
                where l.EmployeeId == me.Id && p.Status != PayrollPeriodStatus.Reopened
                orderby p.From descending
                select new { Line = l, p.BranchId, p.From, p.To, p.Status })
            .Take(24)
            .ToListAsync(ct);

        return rows.Select(x => new PayslipResponse(
            x.Line.PeriodId, x.BranchId, x.From, x.To, x.Status, x.Line.WorkedDays, x.Line.RegularHours, x.Line.OvertimeHours, x.Line.ReplacementHours, x.Line.BasePay,
            x.Line.OvertimePay, x.Line.ReplacementPay, x.Line.Adjustments, x.Line.GrossPay, x.Line.NetPay, x.Line.BankName,
            x.Line.BankAccountNumber is { Length: >= 4 } number ? $"••••{number[^4..]}" : null, x.Line.IsPaid, x.Line.PaidAtUtc, x.Line.PayoutMethod,
            x.Line.PayoutReference)).ToList();
    }

    private static async Task<IResult> SetMyBankAccountAsync(BankAccountRequest r, WorkforceSchedulingDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var me = await EmployeeLookup.CurrentAsync(db, user, ct);
        return await ApplyBankAccountAsync(me, r, db, ct);
    }

    private static async Task<IResult> SetBankAccountAsync(Guid id, BankAccountRequest r, WorkforceSchedulingDbContext db, ICurrentUser user, CancellationToken ct) =>
        await ApplyBankAccountAsync(await EmployeeEndpoints.FindAsync(db, user, id, ct), r, db, ct);

    /// <summary>The account is copied into a period when it is finalized; changing it later does not change a finalized period.</summary>
    private static async Task<IResult> ApplyBankAccountAsync(Employee employee, BankAccountRequest r, WorkforceSchedulingDbContext db, CancellationToken ct)
    {
        var parts = new[] { r.BankName, r.AccountNumber, r.AccountName }.Count(p => !string.IsNullOrWhiteSpace(p));
        var errors = new ValidationErrors();
        if (parts is not (0 or 3))
        {
            errors.Add("AccountNumber", "Give the bank, the account number and the account name together (or none of them to clear the account).");
        }

        CommonRules.MaxLength(errors, "BankName", r.BankName, "Bank", Employee.BankNameMaxLength);
        CommonRules.MaxLength(errors, "AccountName", r.AccountName, "Account name", Employee.BankAccountNameMaxLength);
        if (!string.IsNullOrWhiteSpace(r.AccountNumber) && (r.AccountNumber.Trim().Length is < 6 or > Employee.BankAccountNumberMaxLength || !r.AccountNumber.Trim().All(char.IsAsciiLetterOrDigit)))
        {
            errors.Add("AccountNumber", "The account number has 6-30 letters or digits.");
        }

        errors.ThrowIfAny();

        employee.SetBankAccount(r.BankName, r.AccountNumber, r.AccountName);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { employee.Id, employee.BankName, employee.BankAccountNumber, employee.BankAccountName });
    }

    // ------------------------------------------------------------------ helpers
    private static async Task<PayrollPeriod> FindAsync(WorkforceSchedulingDbContext db, ICurrentUser user, Guid id, CancellationToken ct)
    {
        var tenantId = TenantScope.Require(user);
        var period = await db.PayrollPeriods.Include(p => p.Lines).FirstOrDefaultAsync(p => p.Id == id && p.TenantId == tenantId, ct)
            ?? throw new NotFoundException("Payroll period", id);
        BranchAccess.EnsureCanAccess(user, period.BranchId);
        return period;
    }

    private static object Snapshot(PayrollPeriod p) => new
    {
        p.BranchId, p.From, p.To, Status = p.Status.ToString(), p.TotalNet, p.ReopenReason,
        Paid = p.Lines.Where(l => l.IsPaid).Select(l => new { l.EmployeeId, l.NetPay, Method = l.PayoutMethod.ToString(), l.PayoutReference }).ToList(),
    };

    private static Task Audit(IEventBus bus, ICurrentUser user, ILoggerFactory loggers, Guid tenantId, string action, PayrollPeriod period, object? before) =>
        AuditTrail.PublishAsync(bus, user, loggers.CreateLogger("AICOS.Workforce.Payroll"), tenantId, action, "PayrollPeriod", period.Id, before, Snapshot(period));
}
