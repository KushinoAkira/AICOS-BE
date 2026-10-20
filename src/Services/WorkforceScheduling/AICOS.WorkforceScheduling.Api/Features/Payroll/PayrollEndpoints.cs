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
    decimal TotalPay);

public sealed record PayrollTotals(decimal BasePay, decimal OvertimePay, decimal ReplacementPay, decimal TotalPay, int UnreviewedRecords);

public sealed record PayrollSummaryResponse(DateOnly From, DateOnly To, Guid? BranchId, PayRuleResponse Rules, List<PayrollLine> Lines, PayrollTotals Totals);

/// <summary>
/// Pay calculation (BR-53), a pure function of the pay rules and the approved attendance records (BR-54). Full-time staff earn a day
/// rate for each working day; part-time staff earn an hourly rate, with hours beyond the daily threshold multiplied; every hour of an
/// approved replacement shift earns the replacement rate, whatever the contract type (BR-52). Unreviewed records are not paid and are counted.
/// </summary>
public static class PayrollCalculator
{
    public static PayrollLine Calculate(Employee employee, IReadOnlyList<ShiftAssignment> records, PayRule rules, Func<ShiftAssignment, bool> hasEnded)
    {
        var payable = records.Where(a => a.CountsForPayroll && !a.IsAbsent && a.WorkedHours > 0).ToList();
        var regularByDate = payable.Where(a => !a.IsReplacement).GroupBy(a => a.WorkDate).ToDictionary(g => g.Key, g => g.Sum(a => a.WorkedHours));
        var replacementHours = payable.Where(a => a.IsReplacement).Sum(a => a.WorkedHours);

        var days = regularByDate.Count;
        double regular = 0, overtime = 0;
        decimal basePay, overtimePay = 0;

        if (employee.ContractType == ContractType.FullTime)
        {
            regular = regularByDate.Values.Sum();
            basePay = days * rules.FullTimeDayRate;
        }
        else
        {
            var threshold = (double)rules.OvertimeAfterHours;
            foreach (var hours in regularByDate.Values)
            {
                regular += Math.Min(hours, threshold);
                overtime += Math.Max(0, hours - threshold);
            }

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
            basePay + overtimePay + replacementPay);
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
        WorkforceSchedulingDbContext db, ICurrentUser user, IBranchDirectory directory, ShopClock shop, Guid? branchId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var summary = await BuildAsync(db, user, directory, shop, branchId, from, to, ct);

        var csv = new StringBuilder();
        csv.AppendLine("Employee,Contract,Worked days,Regular hours,Overtime hours,Replacement hours,Absences,Unreviewed records,Base pay,Overtime pay,Replacement pay,Total pay");
        foreach (var l in summary.Lines)
        {
            csv.AppendLine(string.Join(',',
                Escape(l.EmployeeName), l.ContractType, l.WorkedDays, Number(l.RegularHours), Number(l.OvertimeHours), Number(l.ReplacementHours),
                l.Absences, l.UnreviewedRecords, Number(l.BasePay), Number(l.OvertimePay), Number(l.ReplacementPay), Number(l.TotalPay)));
        }

        csv.AppendLine(string.Join(',', "TOTAL", "", "", "", "", "", "", summary.Totals.UnreviewedRecords, Number(summary.Totals.BasePay), Number(summary.Totals.OvertimePay),
            Number(summary.Totals.ReplacementPay), Number(summary.Totals.TotalPay)));

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
        var employeeIds = assignments.Select(a => a.EmployeeId).Distinct().ToList();
        var employees = await db.Employees.AsNoTracking().Where(e => employeeIds.Contains(e.Id)).ToListAsync(ct);
        var now = shop.UtcNow;

        var lines = employees
            .Select(e => PayrollCalculator.Calculate(e, assignments.Where(a => a.EmployeeId == e.Id).ToList(), rules, a => shop.ToUtc(a.WorkDate, a.EndTime) <= now))
            .OrderBy(l => l.EmployeeName)
            .ToList();

        var totals = new PayrollTotals(
            lines.Sum(l => l.BasePay), lines.Sum(l => l.OvertimePay), lines.Sum(l => l.ReplacementPay), lines.Sum(l => l.TotalPay), lines.Sum(l => l.UnreviewedRecords));
        return new PayrollSummaryResponse(from!.Value, to!.Value, branch, PayRuleResponse.From(rules), lines, totals);
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
