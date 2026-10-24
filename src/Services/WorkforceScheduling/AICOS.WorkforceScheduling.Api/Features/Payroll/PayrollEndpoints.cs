using System.Globalization;
using System.Text;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Security;
using AICOS.BuildingBlocks.Validation;
using AICOS.WorkforceScheduling.Api.Domain;
using AICOS.WorkforceScheduling.Api.Infrastructure;
using AICOS.WorkforceScheduling.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.WorkforceScheduling.Api.Features.Payroll;

/// <summary>PUT /pay-rules: the whole rule set of the brand. Rates are VND.</summary>
public sealed record PayRuleRequest(
    decimal? FullTimeDayRate, decimal? PartTimeHourRate, decimal? ReplacementHourRate, decimal? OvertimeAfterHours, decimal? OvertimeMultiplier);

/// <summary><see cref="IsConfigured"/> is false until the Brand Admin saves the rules for the first time.</summary>
public sealed record PayRuleResponse(
    bool IsConfigured, decimal? FullTimeDayRate, decimal? PartTimeHourRate, decimal? ReplacementHourRate, decimal? OvertimeAfterHours, decimal? OvertimeMultiplier)
{
    public static PayRuleResponse From(PayRule r) =>
        new(true, r.FullTimeDayRate, r.PartTimeHourRate, r.ReplacementHourRate, r.OvertimeAfterHours, r.OvertimeMultiplier);
}

public sealed record PayrollLine(
    Guid EmployeeId,
    string EmployeeName,
    Guid BranchId,
    ContractType ContractType,
    int WorkedDays,
    double RegularHours,
    double OvertimeHours,
    double ReplacementHours,
    int Absences,
    int UnreviewedRecords,
    decimal BasePay,
    decimal OvertimePay,
    decimal ReplacementPay,
    decimal Adjustments,
    decimal TotalPay);

/// <summary>POST /payroll/adjustments. Amount is whole VND: positive adds to the pay, negative deducts.</summary>
public sealed record PayrollAdjustmentRequest(Guid? EmployeeId, DateOnly? WorkDate, decimal? Amount, string? Reason);

public sealed record PayrollAdjustmentResponse(Guid Id, Guid EmployeeId, string EmployeeName, Guid BranchId, DateOnly WorkDate, decimal Amount, string Reason, DateTime CreatedAtUtc, Guid? CreatedBy)
{
    public static PayrollAdjustmentResponse From(PayrollAdjustment a, string employeeName) =>
        new(a.Id, a.EmployeeId, employeeName, a.BranchId, a.WorkDate, a.Amount, a.Reason, a.CreatedAtUtc, a.CreatedBy);
}

public sealed record PayrollTotals(decimal BasePay, decimal OvertimePay, decimal ReplacementPay, decimal Adjustments, decimal TotalPay, int UnreviewedRecords);

public sealed record PayrollSummaryResponse(DateOnly From, DateOnly To, Guid? BranchId, PayRuleResponse Rules, List<PayrollLine> Lines, PayrollTotals Totals);

/// <summary>
/// Pay calculation (BR-53), a pure function of the pay rules and the approved attendance records (BR-54). Full-time staff earn a day
/// rate for each working day; part-time staff earn an hourly rate, with hours beyond the daily threshold multiplied; every hour of an
/// approved replacement shift earns the replacement rate, whatever the contract type (BR-52). Unreviewed records are not paid and are counted.
/// </summary>
public static class PayrollCalculator
{
    public static PayrollLine Calculate(
        Employee employee, IReadOnlyList<ShiftAssignment> records, PayRule rules, Func<ShiftAssignment, bool> hasEnded, decimal adjustments = 0)
    {
        var payable = records.Where(a => a.CountsForPayroll && !a.IsAbsent && a.WorkedHours > 0).ToList();
        var regularByDate = payable.Where(a => !a.IsReplacement).GroupBy(a => a.WorkDate).ToDictionary(g => g.Key, g => g.Sum(a => a.WorkedHours));
        var replacementHours = payable.Where(a => a.IsReplacement).Sum(a => a.WorkedHours);

        var days = regularByDate.Count;
        double regular = 0, overtime = 0;
        decimal basePay, overtimePay;

        // Hours beyond the daily threshold are overtime for everyone (BR-53, "applicable overtime requirements").
        var threshold = (double)rules.OvertimeAfterHours;
        foreach (var hours in regularByDate.Values)
        {
            regular += Math.Min(hours, threshold);
            overtime += Math.Max(0, hours - threshold);
        }

        if (employee.ContractType == ContractType.FullTime)
        {
            // The day rate covers the first threshold hours of the day; overtime is paid at the hourly equivalent of that rate.
            basePay = days * rules.FullTimeDayRate;
            overtimePay = (decimal)overtime * (rules.FullTimeDayRate / rules.OvertimeAfterHours) * rules.OvertimeMultiplier;
        }
        else
        {
            basePay = (decimal)regular * rules.PartTimeHourRate;
            overtimePay = (decimal)overtime * rules.PartTimeHourRate * rules.OvertimeMultiplier;
        }

        var replacementPay = (decimal)replacementHours * rules.ReplacementHourRate;
        basePay = Money(basePay);
        overtimePay = Money(overtimePay);
        replacementPay = Money(replacementPay);

        return new PayrollLine(
            employee.Id,
            employee.FullName,
            employee.BranchId,
            employee.ContractType,
            days,
            Math.Round(regular, 2),
            Math.Round(overtime, 2),
            Math.Round(replacementHours, 2),
            records.Count(a => a.IsAbsent && a.ReviewStatus == AttendanceReviewStatus.Approved),
            records.Count(a => ShiftAssignment.ActiveStatuses.Contains(a.Status) && a.ReviewStatus == AttendanceReviewStatus.Unreviewed && hasEnded(a)),
            basePay,
            overtimePay,
            replacementPay,
            adjustments,
            basePay + overtimePay + replacementPay + adjustments);
    }

    private static decimal Money(decimal value) => Math.Round(value, 0, MidpointRounding.AwayFromZero);
}

/// <summary>
/// UC-19 Configure Staff Pay Rules and UC-32 Review Payroll Summary and Export Work/Pay Records. Routes: /api/v1/workforce/pay-rules
/// and /api/v1/workforce/payroll. Pay rules: BrandAdmin writes, BranchManager reads. Payroll: BranchManager for their branch, BrandAdmin
/// for any branch or the whole brand. Only approved attendance counts (BR-54); a period with no pay rules configured is refused.
/// </summary>
public static class PayrollEndpoints
{
    public const int MaxPeriodDays = 62;

    public static void MapPayrollEndpoints(this RouteGroupBuilder api)
    {
        var rules = api.MapGroup("/pay-rules").WithTags("Pay rules");
        rules.MapGet("/", GetRulesAsync).RequireAuthorization(AicosPolicies.Management);
        rules.MapPut("/", UpdateRulesAsync).RequireAuthorization(AicosPolicies.AdminOnly);

        var payroll = api.MapGroup("/payroll").WithTags("Payroll");
        payroll.MapGet("/summary", SummaryAsync).RequireAuthorization(AicosPolicies.Management);
        payroll.MapGet("/export", ExportAsync).RequireAuthorization(AicosPolicies.Management);
        payroll.MapGet("/records/export", ExportRecordsAsync).RequireAuthorization(AicosPolicies.Management);
        payroll.MapGet("/adjustments", ListAdjustmentsAsync).RequireAuthorization(AicosPolicies.Management);
        payroll.MapPost("/adjustments", AddAdjustmentAsync).RequireAuthorization(AicosPolicies.Management);
        payroll.MapDelete("/adjustments/{id:guid}", RemoveAdjustmentAsync).RequireAuthorization(AicosPolicies.Management);
    }

    // ------------------------------------------------------------------ pay rules
    private static async Task<PayRuleResponse> GetRulesAsync(WorkforceSchedulingDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var tenantId = TenantScope.Require(user);
        var rule = await db.PayRules.AsNoTracking().FirstOrDefaultAsync(r => r.TenantId == tenantId, ct);
        return rule is null ? new PayRuleResponse(false, null, null, null, null, null) : PayRuleResponse.From(rule);
    }

    private static async Task<PayRuleResponse> UpdateRulesAsync(
        PayRuleRequest r, WorkforceSchedulingDbContext db, ICurrentUser user, IEventBus bus, ILoggerFactory loggers, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        if (r.FullTimeDayRate is null or <= 0 or > 100_000_000m)
        {
            errors.Add("FullTimeDayRate", "Full-time day rate must be greater than 0 and at most 100,000,000 VND.");
        }

        if (r.PartTimeHourRate is null or <= 0 or > 10_000_000m)
        {
            errors.Add("PartTimeHourRate", "Part-time hourly rate must be greater than 0 and at most 10,000,000 VND.");
        }

        if (r.ReplacementHourRate is null or <= 0 or > 10_000_000m)
        {
            errors.Add("ReplacementHourRate", "Replacement-shift hourly rate must be greater than 0 and at most 10,000,000 VND.");
        }

        if (r.OvertimeAfterHours is null or < 1 or > 24)
        {
            errors.Add("OvertimeAfterHours", "Overtime starts after 1-24 hours a day.");
        }

        if (r.OvertimeMultiplier is null or < 1 or > 5)
        {
            errors.Add("OvertimeMultiplier", "The overtime multiplier must be between 1 and 5.");
        }

        errors.ThrowIfAny();

        var tenantId = TenantScope.Require(user);
        var rule = await db.PayRules.FirstOrDefaultAsync(x => x.TenantId == tenantId, ct);
        var before = rule is null ? null : Snapshot(rule);
        if (rule is null)
        {
            rule = new PayRule(tenantId, r.FullTimeDayRate!.Value, r.PartTimeHourRate!.Value, r.ReplacementHourRate!.Value, r.OvertimeAfterHours!.Value, r.OvertimeMultiplier!.Value);
            db.PayRules.Add(rule);
        }
        else
        {
            rule.Update(r.FullTimeDayRate!.Value, r.PartTimeHourRate!.Value, r.ReplacementHourRate!.Value, r.OvertimeAfterHours!.Value, r.OvertimeMultiplier!.Value);
        }

        await db.SaveChangesAsync(ct);
        await AuditTrail.PublishAsync(
            bus, user, loggers.CreateLogger("AICOS.Workforce.PayRules"), tenantId, "pay_rules.updated", "PayRule", tenantId, before, Snapshot(rule));
        return PayRuleResponse.From(rule);
    }

    private static object Snapshot(PayRule r) => new
    {
        r.FullTimeDayRate, r.PartTimeHourRate, r.ReplacementHourRate, r.OvertimeAfterHours, r.OvertimeMultiplier,
    };

    // ------------------------------------------------------------------ payroll
    private static async Task<PayrollSummaryResponse> SummaryAsync(
        WorkforceSchedulingDbContext db, ICurrentUser user, IBranchDirectory directory, ShopClock shop, Guid? branchId, DateOnly? from, DateOnly? to, CancellationToken ct) =>
        await BuildAsync(db, user, directory, shop, branchId, from, to, ct);

    /// <summary>The same figures as a CSV file (UTF-8 with a byte-order mark, so Excel reads the Vietnamese names correctly).</summary>
    private static async Task<IResult> ExportAsync(
        WorkforceSchedulingDbContext db, ICurrentUser user, IBranchDirectory directory, ShopClock shop, Guid? branchId, DateOnly? from, DateOnly? to, string? format, CancellationToken ct)
    {
        var summary = await BuildAsync(db, user, directory, shop, branchId, from, to, ct);

        if (IsExcel(format))
        {
            return Results.File(ExcelExport.Payroll(summary), ExcelExport.ContentType, $"payroll-{summary.From:yyyyMMdd}-{summary.To:yyyyMMdd}.xlsx");
        }

        var csv = new StringBuilder();
        csv.AppendLine("Employee,Contract,Worked days,Regular hours,Overtime hours,Replacement hours,Absences,Unreviewed records,Base pay,Overtime pay,Replacement pay,Adjustments,Total pay");
        foreach (var l in summary.Lines)
        {
            csv.AppendLine(string.Join(',',
                Escape(l.EmployeeName), l.ContractType, l.WorkedDays, Number(l.RegularHours), Number(l.OvertimeHours), Number(l.ReplacementHours),
                l.Absences, l.UnreviewedRecords, Number(l.BasePay), Number(l.OvertimePay), Number(l.ReplacementPay), Number(l.Adjustments), Number(l.TotalPay)));
        }

        csv.AppendLine(string.Join(',', "TOTAL", "", "", "", "", "", "", summary.Totals.UnreviewedRecords, Number(summary.Totals.BasePay), Number(summary.Totals.OvertimePay),
            Number(summary.Totals.ReplacementPay), Number(summary.Totals.Adjustments), Number(summary.Totals.TotalPay)));

        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();
        return Results.File(bytes, "text/csv; charset=utf-8", $"payroll-{summary.From:yyyyMMdd}-{summary.To:yyyyMMdd}.csv");
    }

    private static async Task<PayrollSummaryResponse> BuildAsync(
        WorkforceSchedulingDbContext db, ICurrentUser user, IBranchDirectory directory, ShopClock shop, Guid? branchId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        if (from is null || to is null)
        {
            errors.Add("from", "Both from and to dates are required (yyyy-MM-dd).");
        }
        else if (from > to)
        {
            errors.Add("from", "'from' must not be later than 'to'.");
        }
        else if (to.Value.DayNumber - from.Value.DayNumber >= MaxPeriodDays)
        {
            errors.Add("from", $"A payroll period covers at most {MaxPeriodDays} days.");
        }

        errors.ThrowIfAny();

        var tenantId = TenantScope.Require(user);
        var branch = await WorkforceScope.FilterAsync(user, directory, branchId, ct);
        var rules = await db.PayRules.AsNoTracking().FirstOrDefaultAsync(r => r.TenantId == tenantId, ct)
            ?? throw new BusinessRuleException("payroll.rules_missing", "The Brand Admin has not configured the pay rules yet.");

        var query = db.ShiftAssignments.AsNoTracking().Where(a => a.TenantId == tenantId && a.WorkDate >= from && a.WorkDate <= to);
        if (branch is not null)
        {
            query = query.Where(a => a.BranchId == branch);
        }

        var assignments = await query.ToListAsync(ct);
        var adjustmentQuery = db.PayrollAdjustments.AsNoTracking().Where(a => a.TenantId == tenantId && a.WorkDate >= from && a.WorkDate <= to);
        if (branch is not null)
        {
            adjustmentQuery = adjustmentQuery.Where(a => a.BranchId == branch);
        }

        var adjustments = await adjustmentQuery.ToListAsync(ct);
        var employeeIds = assignments.Select(a => a.EmployeeId).Concat(adjustments.Select(a => a.EmployeeId)).Distinct().ToList();
        var employees = await db.Employees.AsNoTracking().Where(e => employeeIds.Contains(e.Id)).ToListAsync(ct);
        var now = shop.UtcNow;

        var lines = employees
            .Select(e => PayrollCalculator.Calculate(e, assignments.Where(a => a.EmployeeId == e.Id).ToList(), rules, a => shop.ToUtc(a.WorkDate, a.EndTime) <= now, adjustments.Where(x => x.EmployeeId == e.Id).Sum(x => x.Amount)))
            .OrderBy(l => l.EmployeeName)
            .ToList();

        var totals = new PayrollTotals(
            lines.Sum(l => l.BasePay), lines.Sum(l => l.OvertimePay), lines.Sum(l => l.ReplacementPay), lines.Sum(l => l.Adjustments), lines.Sum(l => l.TotalPay), lines.Sum(l => l.UnreviewedRecords));
        return new PayrollSummaryResponse(from!.Value, to!.Value, branch, PayRuleResponse.From(rules), lines, totals);
    }

    // ------------------------------------------------------------------ work records export (FR-28)
    /// <summary>One CSV row per scheduled shift of the period: what was scheduled, what was recorded, how it was reviewed and whether it is paid.</summary>
    private static async Task<IResult> ExportRecordsAsync(
        WorkforceSchedulingDbContext db, ICurrentUser user, IBranchDirectory directory, ShopClock shop, Guid? branchId, DateOnly? from, DateOnly? to, string? format, CancellationToken ct)
    {
        ValidatePeriod(from, to);
        var tenantId = TenantScope.Require(user);
        var branch = await WorkforceScope.FilterAsync(user, directory, branchId, ct);
        var query = db.ShiftAssignments.AsNoTracking().Where(a => a.TenantId == tenantId && a.WorkDate >= from && a.WorkDate <= to);
        if (branch is not null)
        {
            query = query.Where(a => a.BranchId == branch);
        }

        var assignments = await query.OrderBy(a => a.WorkDate).ThenBy(a => a.StartTime).ToListAsync(ct);
        var employeeIds = assignments.Select(a => a.EmployeeId).Distinct().ToList();
        var names = await db.Employees.AsNoTracking().Where(e => employeeIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.FullName, ct);

        if (IsExcel(format))
        {
            var records = assignments.Select(a => new ExcelExport.RecordRow(
                a.WorkDate, names.GetValueOrDefault(a.EmployeeId, "?"), a.StartTime.ToString("HH:mm", CultureInfo.InvariantCulture), a.EndTime.ToString("HH:mm", CultureInfo.InvariantCulture),
                a.Status, a.IsReplacement, Local(shop, a.EffectiveCheckInUtc), Local(shop, a.EffectiveCheckOutUtc), a.WorkedHours, a.IsAbsent, a.ReviewStatus, a.CountsForPayroll)).ToList();
            return Results.File(ExcelExport.Records(records), ExcelExport.ContentType, $"work-records-{from:yyyyMMdd}-{to:yyyyMMdd}.xlsx");
        }

        var csv = new StringBuilder();
        csv.AppendLine("Date,Employee,Shift start,Shift end,Assignment,Replacement,Check-in,Check-out,Worked hours,Absent,Review,Counts for payroll");
        foreach (var a in assignments)
        {
            csv.AppendLine(string.Join(',',
                a.WorkDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Escape(names.GetValueOrDefault(a.EmployeeId, "?")),
                a.StartTime.ToString("HH:mm", CultureInfo.InvariantCulture), a.EndTime.ToString("HH:mm", CultureInfo.InvariantCulture), a.Status,
                a.IsReplacement ? "yes" : "no", Local(shop, a.EffectiveCheckInUtc), Local(shop, a.EffectiveCheckOutUtc), Number(a.WorkedHours),
                a.IsAbsent ? "yes" : "no", a.ReviewStatus, a.CountsForPayroll ? "yes" : "no"));
        }

        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();
        return Results.File(bytes, "text/csv; charset=utf-8", $"work-records-{from:yyyyMMdd}-{to:yyyyMMdd}.csv");
    }

    private static bool IsExcel(string? format) => string.Equals(format, "xlsx", StringComparison.OrdinalIgnoreCase);

    private static string Local(ShopClock shop, DateTime? utc) =>
        utc is null ? string.Empty : shop.ToLocal(utc.Value).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ payroll adjustments (BR-56)
    private static async Task<List<PayrollAdjustmentResponse>> ListAdjustmentsAsync(
        WorkforceSchedulingDbContext db, ICurrentUser user, IBranchDirectory directory, Guid? branchId, Guid? employeeId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        ValidatePeriod(from, to);
        var tenantId = TenantScope.Require(user);
        var branch = await WorkforceScope.FilterAsync(user, directory, branchId, ct);
        var query = db.PayrollAdjustments.AsNoTracking().Where(a => a.TenantId == tenantId && a.WorkDate >= from && a.WorkDate <= to);
        if (branch is not null)
        {
            query = query.Where(a => a.BranchId == branch);
        }

        if (employeeId is not null)
        {
            query = query.Where(a => a.EmployeeId == employeeId);
        }

        var rows = await query.OrderBy(a => a.WorkDate).ThenBy(a => a.CreatedAtUtc).ToListAsync(ct);
        var ids = rows.Select(a => a.EmployeeId).Distinct().ToList();
        var names = await db.Employees.AsNoTracking().Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.FullName, ct);
        return rows.Select(a => PayrollAdjustmentResponse.From(a, names.GetValueOrDefault(a.EmployeeId, "?"))).ToList();
    }

    /// <summary>A bonus or deduction for one employee on one day. Counts in the payroll of the period that covers the day. Audited.</summary>
    private static async Task<IResult> AddAdjustmentAsync(
        PayrollAdjustmentRequest r, WorkforceSchedulingDbContext db, ICurrentUser user, ShopClock shop, IEventBus bus, ILoggerFactory loggers, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        CommonRules.RequiredId(errors, "EmployeeId", r.EmployeeId, "Employee");
        CommonRules.Required(errors, "Reason", r.Reason, "Reason", PayrollAdjustment.ReasonMaxLength);
        if (r.WorkDate is null)
        {
            errors.Add("WorkDate", "WorkDate is required (yyyy-MM-dd).");
        }
        else if (r.WorkDate > shop.Today)
        {
            errors.Add("WorkDate", "An adjustment cannot be dated in the future.");
        }

        if (r.Amount is null or 0 || Math.Abs(r.Amount.Value) > MaxAdjustment || r.Amount != decimal.Truncate(r.Amount.Value))
        {
            errors.Add("Amount", $"Amount is a whole number of VND, not 0, and at most {MaxAdjustment:N0} up or down.");
        }

        errors.ThrowIfAny();

        var tenantId = TenantScope.Require(user);
        var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == r.EmployeeId && e.TenantId == tenantId, ct)
            ?? throw new NotFoundException("Employee", r.EmployeeId!);
        BranchAccess.EnsureCanAccess(user, employee.BranchId);

        var adjustment = new PayrollAdjustment(tenantId, employee.BranchId, employee.Id, r.WorkDate!.Value, r.Amount!.Value, r.Reason!.Trim());
        db.PayrollAdjustments.Add(adjustment);
        await db.SaveChangesAsync(ct);
        await AuditTrail.PublishAsync(
            bus, user, loggers.CreateLogger("AICOS.Workforce.Payroll"), tenantId, "payroll_adjustment.added", "PayrollAdjustment", adjustment.Id, null, AdjustmentSnapshot(adjustment));

        return Results.Created($"/api/v1/workforce/payroll/adjustments/{adjustment.Id}", PayrollAdjustmentResponse.From(adjustment, employee.FullName));
    }

    private static async Task<IResult> RemoveAdjustmentAsync(
        Guid id, WorkforceSchedulingDbContext db, ICurrentUser user, IEventBus bus, ILoggerFactory loggers, CancellationToken ct)
    {
        var tenantId = TenantScope.Require(user);
        var adjustment = await db.PayrollAdjustments.FirstOrDefaultAsync(a => a.Id == id && a.TenantId == tenantId, ct) ?? throw new NotFoundException("Payroll adjustment", id);
        BranchAccess.EnsureCanAccess(user, adjustment.BranchId);

        var before = AdjustmentSnapshot(adjustment);
        db.PayrollAdjustments.Remove(adjustment);
        await db.SaveChangesAsync(ct);
        await AuditTrail.PublishAsync(bus, user, loggers.CreateLogger("AICOS.Workforce.Payroll"), tenantId, "payroll_adjustment.removed", "PayrollAdjustment", id, before);
        return Results.NoContent();
    }

    private static object AdjustmentSnapshot(PayrollAdjustment a) => new { a.EmployeeId, a.BranchId, a.WorkDate, a.Amount, a.Reason };

    private const decimal MaxAdjustment = 50_000_000m;

    private static void ValidatePeriod(DateOnly? from, DateOnly? to)
    {
        if (from is null || to is null)
        {
            throw new ValidationFailedException("from", "Both from and to dates are required (yyyy-MM-dd).");
        }

        if (from > to || to.Value.DayNumber - from.Value.DayNumber >= MaxPeriodDays)
        {
            throw new ValidationFailedException("from", $"The period must be ordered and at most {MaxPeriodDays} days.");
        }
    }

    private static string Number(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Quotes a text field and neutralises spreadsheet formulas, so an employee name cannot run code when the file is opened.</summary>
    private static string Escape(string value)
    {
        var safe = value.Length > 0 && "=+-@\t\r".Contains(value[0]) ? "'" + value : value;
        return $"\"{safe.Replace("\"", "\"\"")}\"";
    }
}
