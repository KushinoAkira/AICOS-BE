using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Persistence;
using AICOS.BuildingBlocks.Security;
using AICOS.BuildingBlocks.Validation;
using AICOS.WorkforceScheduling.Api.Domain;
using AICOS.WorkforceScheduling.Api.Infrastructure;
using AICOS.WorkforceScheduling.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.WorkforceScheduling.Api.Features.Employees;

/// <summary>
/// <see cref="UserId"/> = id of the Barista or Cashier login account created in Identity. <see cref="FullName"/> defaults to the
/// name of that account. <see cref="BranchId"/> is only read for a BrandAdmin.
/// </summary>
public sealed record EmployeeRequest(Guid? BranchId, Guid? UserId, string? FullName, string? Position, DateOnly? HireDate, ContractType? ContractType);

public sealed record UpdateEmployeeRequest(string? FullName, string? Position, DateOnly? HireDate, ContractType? ContractType);

public sealed record ChangeEmployeeStatusRequest(EmployeeStatus? Status);

public sealed record EmployeeResponse(
    Guid Id, Guid BranchId, Guid UserId, string FullName, string Role, string? Position, DateOnly? HireDate, ContractType ContractType, EmployeeStatus Status)
{
    public static EmployeeResponse From(Employee e) =>
        new(e.Id, e.BranchId, e.UserId, e.FullName, e.Role, e.Position, e.HireDate, e.ContractType, e.Status);
}

public sealed record WorkShiftRequest(Guid? BranchId, string? Name, TimeOnly? StartTime, TimeOnly? EndTime);

public sealed record ChangeWorkShiftStatusRequest(bool? IsActive);

public sealed record WorkShiftResponse(Guid Id, Guid BranchId, string Name, TimeOnly StartTime, TimeOnly EndTime, double Hours, bool IsActive)
{
    public static WorkShiftResponse From(WorkShift s) => new(s.Id, s.BranchId, s.Name, s.StartTime, s.EndTime, s.Hours, s.IsActive);
}

/// <summary>
/// UC-27 Manage Branch Staff (HR profiles with the contract type) and the shift templates used for scheduling (UC-28).
/// Routes: /api/v1/workforce/employees and /work-shifts. Manage: BranchManager of the branch, or the BrandAdmin for any branch of the
/// brand. Staff read their own profile and the shift templates of their branch.
/// </summary>
public static class EmployeeEndpoints
{
    private static readonly string[] StaffRoles = [AicosRoles.Barista, AicosRoles.Cashier];

    public static void MapEmployeeEndpoints(this RouteGroupBuilder api)
    {
        var employees = api.MapGroup("/employees").WithTags("Employees");
        employees.MapGet("/", ListAsync).RequireAuthorization(AicosPolicies.Management);
        employees.MapGet("/me", MeAsync).RequireAuthorization(AicosPolicies.Staff);
        employees.MapGet("/{id:guid}", GetAsync).RequireAuthorization(AicosPolicies.Management).WithName("GetEmployee");
        employees.MapPost("/", CreateAsync).RequireAuthorization(AicosPolicies.Management);
        employees.MapPut("/{id:guid}", UpdateAsync).RequireAuthorization(AicosPolicies.Management);
        employees.MapPatch("/{id:guid}/status", ChangeStatusAsync).RequireAuthorization(AicosPolicies.Management);

        var shifts = api.MapGroup("/work-shifts").WithTags("Work shifts");
        shifts.MapGet("/", ListShiftsAsync).RequireAuthorization(AicosPolicies.Staff);
        shifts.MapGet("/{id:guid}", GetShiftAsync).RequireAuthorization(AicosPolicies.Staff).WithName("GetWorkShift");
        shifts.MapPost("/", CreateShiftAsync).RequireAuthorization(AicosPolicies.Management);
        shifts.MapPut("/{id:guid}", UpdateShiftAsync).RequireAuthorization(AicosPolicies.Management);
        shifts.MapPatch("/{id:guid}/status", ChangeShiftStatusAsync).RequireAuthorization(AicosPolicies.Management);
    }

    // ------------------------------------------------------------------ employees
    private static async Task<PagedResponse<EmployeeResponse>> ListAsync(
        WorkforceSchedulingDbContext db, ICurrentUser user, IBranchDirectory directory, Guid? branchId, string? search, EmployeeStatus? status,
        ContractType? contractType, int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        var tenantId = TenantScope.Require(user);
        var branch = await WorkforceScope.FilterAsync(user, directory, branchId, ct);
        var query = db.Employees.AsNoTracking().Where(e => e.TenantId == tenantId);
        if (branch is not null)
        {
            query = query.Where(e => e.BranchId == branch);
        }

        if (status is not null)
        {
            query = query.Where(e => e.Status == status);
        }

        if (contractType is not null)
        {
            query = query.Where(e => e.ContractType == contractType);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(e => e.FullName.ToLower().Contains(term) || (e.Position != null && e.Position.ToLower().Contains(term)));
        }

        return await query.OrderBy(e => e.FullName).ToPagedAsync(page, pageSize, EmployeeResponse.From, ct);
    }

    private static async Task<EmployeeResponse> MeAsync(WorkforceSchedulingDbContext db, ICurrentUser user, CancellationToken ct) =>
        EmployeeResponse.From(await EmployeeLookup.CurrentAsync(db, user, ct));

    private static async Task<EmployeeResponse> GetAsync(Guid id, WorkforceSchedulingDbContext db, ICurrentUser user, CancellationToken ct) =>
        EmployeeResponse.From(await FindAsync(db, user, id, ct));

    private static async Task<IResult> CreateAsync(
        EmployeeRequest r, WorkforceSchedulingDbContext db, ICurrentUser user, IBranchDirectory branches, IUserDirectory users, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        CommonRules.RequiredId(errors, "UserId", r.UserId, "User account");
        CommonRules.MaxLength(errors, "FullName", r.FullName, "Full name", 255);
        CommonRules.MaxLength(errors, "Position", r.Position, "Position", 50);
        CommonRules.RequiredEnum(errors, "ContractType", r.ContractType, "Contract type");
        errors.ThrowIfAny();

        var tenantId = TenantScope.Require(user);
        var branchId = await BranchScope.ResolveAsync(user, branches, r.BranchId, requireActive: true, ct);

        var account = await users.GetAsync(r.UserId!.Value, ct);
        if (!StaffRoles.Contains(account.Role))
        {
            throw new ValidationFailedException("UserId", "Only Barista and Cashier accounts have an employee profile.");
        }

        if (account.BranchId != branchId)
        {
            throw new ValidationFailedException("UserId", "The account is assigned to a different branch than the employee profile.");
        }

        if (!account.IsActive)
        {
            throw new ValidationFailedException("UserId", "The account is locked.");
        }

        if (await db.Employees.AnyAsync(e => e.UserId == r.UserId, ct))
        {
            throw new ConflictException("This user account already has an employee profile.", "employee.duplicate_user");
        }

        var name = string.IsNullOrWhiteSpace(r.FullName) ? account.FullName : r.FullName;
        var employee = new Employee(tenantId, branchId, account.Id, name, account.Role, r.Position, r.HireDate, r.ContractType!.Value);
        db.Employees.Add(employee);
        await db.SaveChangesAsync(ct);
        return Results.CreatedAtRoute("GetEmployee", new { id = employee.Id }, EmployeeResponse.From(employee));
    }

    private static async Task<EmployeeResponse> UpdateAsync(
        Guid id, UpdateEmployeeRequest r, WorkforceSchedulingDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        CommonRules.Required(errors, "FullName", r.FullName, "Full name", 255);
        CommonRules.MaxLength(errors, "Position", r.Position, "Position", 50);
        CommonRules.RequiredEnum(errors, "ContractType", r.ContractType, "Contract type");
        errors.ThrowIfAny();

        var employee = await FindAsync(db, user, id, ct);
        employee.Update(r.FullName!, r.Position, r.HireDate, r.ContractType!.Value);
        await db.SaveChangesAsync(ct);
        return EmployeeResponse.From(employee);
    }

    private static async Task<EmployeeResponse> ChangeStatusAsync(
        Guid id, ChangeEmployeeStatusRequest r, WorkforceSchedulingDbContext db, ICurrentUser user, ShopClock shop, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        CommonRules.RequiredEnum(errors, "Status", r.Status, "Status");
        errors.ThrowIfAny();

        var employee = await FindAsync(db, user, id, ct);
        if (r.Status == EmployeeStatus.Inactive)
        {
            var today = shop.Today;
            if (await db.ShiftAssignments.AnyAsync(a => a.EmployeeId == id && a.WorkDate >= today && ShiftAssignment.ActiveStatuses.Contains(a.Status), ct))
            {
                throw new ConflictException("The employee still has upcoming shifts. Reassign or remove them first.", "employee.has_upcoming_shifts");
            }
        }

        employee.ChangeStatus(r.Status!.Value);
        await db.SaveChangesAsync(ct);
        return EmployeeResponse.From(employee);
    }

    /// <summary>An employee of another brand is reported as not found (BR-04); one of another branch is forbidden for branch staff (BR-05).</summary>
    internal static async Task<Employee> FindAsync(WorkforceSchedulingDbContext db, ICurrentUser user, Guid id, CancellationToken ct)
    {
        var tenantId = TenantScope.Require(user);
        var employee = await db.Employees.FirstOrDefaultAsync(e => e.Id == id && e.TenantId == tenantId, ct) ?? throw new NotFoundException("Employee", id);
        BranchAccess.EnsureCanAccess(user, employee.BranchId);
        return employee;
    }

    // ------------------------------------------------------------------ shift templates
    private static async Task<List<WorkShiftResponse>> ListShiftsAsync(
        WorkforceSchedulingDbContext db, ICurrentUser user, IBranchDirectory directory, Guid? branchId, bool? activeOnly, CancellationToken ct)
    {
        var tenantId = TenantScope.Require(user);
        var branch = await WorkforceScope.FilterAsync(user, directory, branchId, ct);
        var query = db.WorkShifts.AsNoTracking().Where(s => s.TenantId == tenantId);
        if (branch is not null)
        {
            query = query.Where(s => s.BranchId == branch);
        }

        if (activeOnly == true)
        {
            query = query.Where(s => s.IsActive);
        }

        var shifts = await query.OrderBy(s => s.BranchId).ThenBy(s => s.StartTime).ToListAsync(ct);
        return shifts.Select(WorkShiftResponse.From).ToList();
    }

    private static async Task<WorkShiftResponse> GetShiftAsync(Guid id, WorkforceSchedulingDbContext db, ICurrentUser user, CancellationToken ct) =>
        WorkShiftResponse.From(await FindShiftAsync(db, user, id, ct));

    private static async Task<IResult> CreateShiftAsync(
        WorkShiftRequest r, WorkforceSchedulingDbContext db, ICurrentUser user, IBranchDirectory directory, CancellationToken ct)
    {
        ValidateShift(r);
        var tenantId = TenantScope.Require(user);
        var branchId = await BranchScope.ResolveAsync(user, directory, r.BranchId, requireActive: true, ct);
        await EnsureShiftNameUniqueAsync(db, branchId, r.Name!, null, ct);

        var shift = new WorkShift(tenantId, branchId, r.Name!, r.StartTime!.Value, r.EndTime!.Value);
        db.WorkShifts.Add(shift);
        await db.SaveChangesAsync(ct);
        return Results.CreatedAtRoute("GetWorkShift", new { id = shift.Id }, WorkShiftResponse.From(shift));
    }

    /// <summary>Times can be changed freely: existing assignments keep the times they were created with.</summary>
    private static async Task<WorkShiftResponse> UpdateShiftAsync(
        Guid id, WorkShiftRequest r, WorkforceSchedulingDbContext db, ICurrentUser user, CancellationToken ct)
    {
        ValidateShift(r);
        var shift = await FindShiftAsync(db, user, id, ct);
        await EnsureShiftNameUniqueAsync(db, shift.BranchId, r.Name!, id, ct);
        shift.Update(r.Name!, r.StartTime!.Value, r.EndTime!.Value);
        await db.SaveChangesAsync(ct);
        return WorkShiftResponse.From(shift);
    }

    private static async Task<WorkShiftResponse> ChangeShiftStatusAsync(
        Guid id, ChangeWorkShiftStatusRequest r, WorkforceSchedulingDbContext db, ICurrentUser user, CancellationToken ct)
    {
        if (r.IsActive is null)
        {
            throw new ValidationFailedException("IsActive", "IsActive is required (true or false).");
        }

        var shift = await FindShiftAsync(db, user, id, ct);
        shift.SetActive(r.IsActive.Value);
        await db.SaveChangesAsync(ct);
        return WorkShiftResponse.From(shift);
    }

    private static void ValidateShift(WorkShiftRequest r)
    {
        var errors = new ValidationErrors();
        CommonRules.Required(errors, "Name", r.Name, "Shift name", 50);
        if (r.StartTime is null)
        {
            errors.Add("StartTime", "Start time is required (HH:mm).");
        }

        if (r.EndTime is null)
        {
            errors.Add("EndTime", "End time is required (HH:mm).");
        }
        else if (r.StartTime is not null)
        {
            var hours = (r.EndTime.Value - r.StartTime.Value).TotalHours;
            if (r.EndTime <= r.StartTime)
            {
                errors.Add("EndTime", "End time must be after start time (overnight shifts are not supported).");
            }
            else if (hours is < 1 or > 12)
            {
                errors.Add("EndTime", "A shift must last between 1 and 12 hours.");
            }
        }

        errors.ThrowIfAny();
    }

    private static async Task EnsureShiftNameUniqueAsync(WorkforceSchedulingDbContext db, Guid branchId, string name, Guid? excludeId, CancellationToken ct)
    {
        var normalized = name.Trim().ToLower();
        if (await db.WorkShifts.AnyAsync(s => s.BranchId == branchId && s.Name.ToLower() == normalized && s.Id != excludeId, ct))
        {
            throw new ConflictException($"Shift '{name.Trim()}' already exists in this branch.", "shift.duplicate_name");
        }
    }

    internal static async Task<WorkShift> FindShiftAsync(WorkforceSchedulingDbContext db, ICurrentUser user, Guid id, CancellationToken ct)
    {
        var tenantId = TenantScope.Require(user);
        var shift = await db.WorkShifts.FirstOrDefaultAsync(s => s.Id == id && s.TenantId == tenantId, ct) ?? throw new NotFoundException("Work shift", id);
        BranchAccess.EnsureCanAccess(user, shift.BranchId);
        return shift;
    }
}
