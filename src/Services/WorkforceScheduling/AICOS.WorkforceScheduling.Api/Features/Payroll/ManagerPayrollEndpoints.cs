using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Security;
using AICOS.BuildingBlocks.Validation;
using AICOS.WorkforceScheduling.Api.Domain;
using AICOS.WorkforceScheduling.Api.Infrastructure;
using AICOS.WorkforceScheduling.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.WorkforceScheduling.Api.Features.Payroll;

/// <summary>PUT /payroll/managers/{userId}/salary: the monthly salary of a branch manager, in whole VND.</summary>
public sealed record ManagerSalaryRequest(decimal? MonthlySalary);

/// <summary>A branch manager with a salary profile.</summary>
public sealed record ManagerSalaryResponse(
    Guid EmployeeId, Guid UserId, string FullName, Guid BranchId, decimal MonthlySalary, EmployeeStatus Status,
    string? BankName, string? BankAccountNumber, string? BankAccountName)
{
    public static ManagerSalaryResponse From(Employee e) => new(
        e.Id, e.UserId, e.FullName, e.BranchId, e.MonthlySalary ?? 0, e.Status, e.BankName, e.BankAccountNumber, e.BankAccountName);
}

/// <summary>A bonus (positive) or deduction (negative) for one manager in the month being finalized.</summary>
public sealed record ManagerAdjustment(Guid EmployeeId, decimal Amount);

/// <summary>POST /payroll/manager-periods: finalize the managers' salaries of a month that is over (<c>Month</c> is yyyy-MM).</summary>
public sealed record FinalizeManagerPayrollRequest(string? Month, List<ManagerAdjustment>? Adjustments);

/// <summary>
/// The brand admin pays the branch managers a fixed monthly salary, the way managers pay their staff: set each manager's salary, finalize a
/// month (with a bonus or deduction per manager), then record the transfers. Paying, reopening and the bank transfer list of a managers period
/// go through /payroll/periods/{id}, like a staff period. Routes under /api/v1/workforce/payroll; brand admin only.
/// </summary>
public static class ManagerPayrollEndpoints
{
    private const string ManagerRole = "BranchManager";
    private const decimal MaxAdjustment = 100_000_000m;

    public static void MapManagerPayrollEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/payroll").WithTags("Manager payroll").RequireAuthorization(AicosPolicies.AdminOnly);
        group.MapGet("/managers", ListManagersAsync);
        group.MapPut("/managers/{userId:guid}/salary", SetSalaryAsync);
        group.MapGet("/manager-periods", ListPeriodsAsync);
        group.MapPost("/manager-periods", FinalizeAsync);
    }

    private static async Task<List<ManagerSalaryResponse>> ListManagersAsync(WorkforceSchedulingDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var tenantId = TenantScope.Require(user);
        var rows = await db.Employees.AsNoTracking().Where(e => e.TenantId == tenantId && e.Role == ManagerRole).OrderBy(e => e.FullName).ToListAsync(ct);
        return rows.Select(ManagerSalaryResponse.From).ToList();
    }

    /// <summary>Creates the manager's salary profile the first time (from the Identity account: name and branch), then only changes the amount.</summary>
    private static async Task<ManagerSalaryResponse> SetSalaryAsync(
        Guid userId, ManagerSalaryRequest r, WorkforceSchedulingDbContext db, ICurrentUser user, IUserDirectory users, IEventBus bus, ILoggerFactory loggers,
        CancellationToken ct)
    {
        var errors = new ValidationErrors();
        if (r.MonthlySalary is null or < 0 || r.MonthlySalary > Employee.MonthlySalaryMax || r.MonthlySalary != decimal.Truncate(r.MonthlySalary.Value))
        {
            errors.Add("MonthlySalary", $"The monthly salary is a whole number of VND from 0 to {Employee.MonthlySalaryMax:N0}.");
        }

        errors.ThrowIfAny();

        var tenantId = TenantScope.Require(user);
        var employee = await db.Employees.FirstOrDefaultAsync(e => e.TenantId == tenantId && e.UserId == userId, ct);
        object? before = employee is null ? null : new { employee.MonthlySalary };
        if (employee is null)
        {
            var account = await users.GetAsync(userId, ct);
            if (account.Role != ManagerRole || account.BranchId is null)
            {
                throw new ValidationFailedException("UserId", "Only a branch manager account of a branch gets a monthly salary here.");
            }

            employee = new Employee(tenantId, account.BranchId.Value, account.Id, account.FullName, ManagerRole, "Quản lý chi nhánh", null, ContractType.FullTime);
            db.Employees.Add(employee);
        }
        else if (!employee.IsManager)
        {
            throw new ValidationFailedException("UserId", "This account is a barista or cashier: they are paid by their shifts.");
        }

        employee.SetMonthlySalary(r.MonthlySalary!.Value);
        await db.SaveChangesAsync(ct);
        await AuditTrail.PublishAsync(
            bus, user, loggers.CreateLogger("AICOS.Workforce.Payroll"), tenantId, "manager_salary.set", "Employee", employee.Id, before, new { employee.MonthlySalary });
        return ManagerSalaryResponse.From(employee);
    }

    private static async Task<List<PayrollPeriodResponse>> ListPeriodsAsync(WorkforceSchedulingDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var tenantId = TenantScope.Require(user);
        var rows = await db.PayrollPeriods.AsNoTracking().Include(p => p.Lines)
            .Where(p => p.TenantId == tenantId && p.Kind == PayrollKind.Managers)
            .OrderByDescending(p => p.From).ThenByDescending(p => p.FinalizedAtUtc)
            .Take(60)
            .ToListAsync(ct);
        return rows.Select(p => PayrollPeriodResponse.Of(p, withLines: false)).ToList();
    }

    /// <summary>
    /// One line per active manager with a salary above 0: the monthly salary plus the bonus or deduction given here. The bank account is
    /// copied like for staff. A month already finalized (and not reopened) is refused.
    /// </summary>
    private static async Task<IResult> FinalizeAsync(
        FinalizeManagerPayrollRequest r, WorkforceSchedulingDbContext db, ICurrentUser user, ShopClock shop, IEventBus bus, ILoggerFactory loggers, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        if (!DateOnly.TryParseExact($"{r.Month}-01", "yyyy-MM-dd", out var from))
        {
            errors.Add("Month", "Month is required as yyyy-MM.");
        }

        foreach (var a in r.Adjustments ?? [])
        {
            if (Math.Abs(a.Amount) > MaxAdjustment || a.Amount != decimal.Truncate(a.Amount))
            {
                errors.Add("Adjustments", $"A bonus or deduction is a whole number of VND, at most {MaxAdjustment:N0} up or down.");
                break;
            }
        }

        errors.ThrowIfAny();

        var to = from.AddMonths(1).AddDays(-1);
        if (to >= shop.Today)
        {
            throw new BusinessRuleException("payroll.period_not_over", "Only a month that is over can be finalized.");
        }

        var tenantId = TenantScope.Require(user);
        if (await db.PayrollPeriods.AnyAsync(p => p.TenantId == tenantId && p.Kind == PayrollKind.Managers && p.Status != PayrollPeriodStatus.Reopened && p.From <= to && p.To >= from, ct))
        {
            throw new ConflictException("The managers' salaries of this month are already finalized.", "payroll.period_overlap");
        }

        var managers = await db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.Role == ManagerRole && e.Status == EmployeeStatus.Active && e.MonthlySalary > 0)
            .OrderBy(e => e.FullName)
            .ToListAsync(ct);
        if (managers.Count == 0)
        {
            throw new BusinessRuleException("payroll.no_managers", "No branch manager has a monthly salary yet. Set the salaries first.");
        }

        var extra = (r.Adjustments ?? []).GroupBy(a => a.EmployeeId).ToDictionary(g => g.Key, g => g.Sum(a => a.Amount));
        if (extra.Keys.Any(id => managers.All(m => m.Id != id)))
        {
            throw new ValidationFailedException("Adjustments", "A bonus or deduction names someone who is not a paid manager of the brand.");
        }

        var period = new PayrollPeriod(tenantId, Guid.Empty, from, to, user.UserId, shop.UtcNow, PayrollKind.Managers);
        foreach (var m in managers)
        {
            var salary = m.MonthlySalary!.Value;
            var adjustment = extra.GetValueOrDefault(m.Id);
            period.AddLine(new PayrollPeriodLine(period.Id, m, 0, 0, 0, 0, salary, 0, 0, adjustment, salary + adjustment));
        }

        db.PayrollPeriods.Add(period);
        await db.SaveChangesAsync(ct);
        await PayrollPeriodEndpoints.Audit(bus, user, loggers, tenantId, "payroll_period.finalized", period, null);
        return Results.Created($"/api/v1/workforce/payroll/periods/{period.Id}", PayrollPeriodResponse.Of(period));
    }
}
