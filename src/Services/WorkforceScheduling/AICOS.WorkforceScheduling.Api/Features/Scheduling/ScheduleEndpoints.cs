using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Persistence;
using AICOS.BuildingBlocks.Security;
using AICOS.BuildingBlocks.Validation;
using AICOS.WorkforceScheduling.Api.Domain;
using AICOS.WorkforceScheduling.Api.Features.Employees;
using AICOS.WorkforceScheduling.Api.Infrastructure;
using AICOS.WorkforceScheduling.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.WorkforceScheduling.Api.Features.Scheduling;

/// <summary><see cref="WorkDate"/> is a shop-local date "yyyy-MM-dd".</summary>
public sealed record AssignShiftRequest(Guid? EmployeeId, Guid? WorkShiftId, DateOnly? WorkDate);

public sealed record DeclineShiftRequest(string? Reason);

public sealed record ShiftAssignmentResponse(
    Guid Id, Guid BranchId, Guid EmployeeId, string EmployeeName, Guid WorkShiftId, string ShiftName, DateOnly WorkDate,
    TimeOnly StartTime, TimeOnly EndTime, AssignmentStatus Status, string? DeclineReason, DateTime? CheckInAtUtc, DateTime? CheckOutAtUtc);

public sealed record SwapRequest(Guid? AssignmentId, Guid? TargetEmployeeId, string? Reason);

public sealed record DecideSwapRequest(string? Note);

public sealed record SwapResponse(
    Guid Id, Guid BranchId, Guid AssignmentId, DateOnly WorkDate, string ShiftName, Guid RequesterEmployeeId, string RequesterName,
    Guid TargetEmployeeId, string TargetName, string Reason, SwapStatus Status, string? ManagerNote, DateTime CreatedAtUtc, DateTime? DecidedAtUtc);

/// <summary>
/// UC-10 Schedule Shifts, UC-32 View/Confirm Shift + Request Swap, UC-11 Approve/Reject Swap.
/// Routes: /api/v1/workforce/shift-assignments and /api/v1/workforce/shift-swaps.
/// BR-22: no overlapping shifts for one employee on the same date. BR-23: a swap takes effect only after Manager approval.
/// Weekly hours are capped by <c>Workforce:MaxWeeklyHours</c> (default 48).
/// </summary>
public static class ScheduleEndpoints
{
    public const int MaxDaysAhead = 60;

    public static void MapScheduleEndpoints(this RouteGroupBuilder api)
    {
        var assignments = api.MapGroup("/shift-assignments").WithTags("Shift assignments");
        assignments.MapGet("/", ListAsync).RequireAuthorization(AicosPolicies.Management);
        assignments.MapGet("/mine", MineAsync).RequireAuthorization(AicosPolicies.Staff);
        assignments.MapGet("/{id:guid}", GetAsync).RequireAuthorization(AicosPolicies.Staff).WithName("GetShiftAssignment");
        assignments.MapPost("/", AssignAsync).RequireAuthorization(AicosPolicies.Management);
        assignments.MapDelete("/{id:guid}", RemoveAsync).RequireAuthorization(AicosPolicies.Management);
        assignments.MapPost("/{id:guid}/confirm", ConfirmAsync).RequireAuthorization(AicosPolicies.Staff);
        assignments.MapPost("/{id:guid}/decline", DeclineAsync).RequireAuthorization(AicosPolicies.Staff);

        var swaps = api.MapGroup("/shift-swaps").WithTags("Shift swaps");
        swaps.MapGet("/", ListSwapsAsync).RequireAuthorization(AicosPolicies.Management);
        swaps.MapGet("/mine", MySwapsAsync).RequireAuthorization(AicosPolicies.Staff);
        swaps.MapPost("/", RequestSwapAsync).RequireAuthorization(AicosPolicies.Staff);
        swaps.MapPost("/{id:guid}/approve", ApproveSwapAsync).RequireAuthorization(AicosPolicies.Management);
        swaps.MapPost("/{id:guid}/reject", RejectSwapAsync).RequireAuthorization(AicosPolicies.Management);
        swaps.MapPost("/{id:guid}/cancel", CancelSwapAsync).RequireAuthorization(AicosPolicies.Staff);
    }

    // ------------------------------------------------------------------ assignments (manager)
    private static async Task<PagedResponse<ShiftAssignmentResponse>> ListAsync(
        WorkforceSchedulingDbContext db, ICurrentUser user, Guid? branchId, DateOnly? from, DateOnly? to, Guid? employeeId,
        AssignmentStatus? status, int page = 1, int pageSize = 50, CancellationToken ct = default)
    {
        var branch = BranchAccess.ResolveFilter(user, branchId);
        var query = db.ShiftAssignments.AsNoTracking();
        if (branch is not null)
        {
            query = query.Where(a => a.BranchId == branch);
        }

        if (from is not null)
        {
            query = query.Where(a => a.WorkDate >= from);
        }

        if (to is not null)
        {
            query = query.Where(a => a.WorkDate <= to);
        }

        if (employeeId is not null)
        {
            query = query.Where(a => a.EmployeeId == employeeId);
        }

        if (status is not null)
        {
            query = query.Where(a => a.Status == status);
        }

        var paged = await query.OrderBy(a => a.WorkDate).ThenBy(a => a.StartTime).ToPagedAsync(page, pageSize, a => a, ct);
        return new PagedResponse<ShiftAssignmentResponse>(await MapAsync(db, paged.Items, ct), paged.Page, paged.PageSize, paged.TotalCount);
    }

    private static async Task<IResult> AssignAsync(
        AssignShiftRequest r, WorkforceSchedulingDbContext db, ICurrentUser user, ShopClock shop, IConfiguration config, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        CommonRules.RequiredId(errors, "EmployeeId", r.EmployeeId, "Employee");
        CommonRules.RequiredId(errors, "WorkShiftId", r.WorkShiftId, "Work shift");
        if (r.WorkDate is null)
        {
            errors.Add("WorkDate", "Work date is required (yyyy-MM-dd).");
        }
        else if (r.WorkDate < shop.Today)
        {
            errors.Add("WorkDate", "Cannot schedule a shift in the past.");
        }
        else if (r.WorkDate > shop.Today.AddDays(MaxDaysAhead))
        {
            errors.Add("WorkDate", $"Shifts can be scheduled at most {MaxDaysAhead} days ahead.");
        }

        errors.ThrowIfAny();

        var employee = await EmployeeEndpoints.FindAsync(db, user, r.EmployeeId!.Value, ct);
        var shift = await EmployeeEndpoints.FindShiftAsync(db, user, r.WorkShiftId!.Value, ct);
        if (employee.BranchId != shift.BranchId)
        {
            throw new ValidationFailedException("WorkShiftId", "The shift belongs to a different branch than the employee.");
        }

        if (employee.Status != EmployeeStatus.Active)
        {
            throw new ValidationFailedException("EmployeeId", "The employee is inactive.");
        }

        if (!shift.IsActive)
        {
            throw new ValidationFailedException("WorkShiftId", "The shift template is inactive.");
        }

        await EnsureCanWorkAsync(db, config, employee.Id, r.WorkDate!.Value, shift.StartTime, shift.EndTime, null, ct);

        var assignment = new ShiftAssignment(employee.BranchId, employee.Id, shift, r.WorkDate!.Value);
        db.ShiftAssignments.Add(assignment);
        await db.SaveChangesAsync(ct);
        return Results.CreatedAtRoute("GetShiftAssignment", new { id = assignment.Id }, (await MapAsync(db, [assignment], ct))[0]);
    }

    private static async Task<IResult> RemoveAsync(Guid id, WorkforceSchedulingDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var assignment = await FindAsync(db, user, id, ct);
        if (assignment.CheckInAtUtc is not null)
        {
            throw new ConflictException("The employee already checked in for this shift; it cannot be removed.", "shift.already_started");
        }

        db.ShiftAssignments.Remove(assignment);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    // ------------------------------------------------------------------ assignments (employee)
    private static async Task<List<ShiftAssignmentResponse>> MineAsync(
        WorkforceSchedulingDbContext db, ICurrentUser user, ShopClock shop, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var me = await EmployeeLookup.CurrentAsync(db, user, ct);
        var start = from ?? shop.Today;
        var end = to ?? start.AddDays(13);
        var items = await db.ShiftAssignments.AsNoTracking()
            .Where(a => a.EmployeeId == me.Id && a.WorkDate >= start && a.WorkDate <= end)
            .OrderBy(a => a.WorkDate).ThenBy(a => a.StartTime)
            .ToListAsync(ct);
        return await MapAsync(db, items, ct);
    }

    private static async Task<ShiftAssignmentResponse> GetAsync(Guid id, WorkforceSchedulingDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var assignment = await FindAsync(db, user, id, ct);
        await EnsureManagerOrOwnerAsync(db, user, assignment, ct);
        return (await MapAsync(db, [assignment], ct))[0];
    }

    private static async Task<ShiftAssignmentResponse> ConfirmAsync(Guid id, WorkforceSchedulingDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var assignment = await FindOwnAsync(db, user, id, ct);
        assignment.Confirm();
        await db.SaveChangesAsync(ct);
        return (await MapAsync(db, [assignment], ct))[0];
    }

    /// <summary>A2: the employee cannot work this shift; the manager sees it as Declined and assigns someone else.</summary>
    private static async Task<ShiftAssignmentResponse> DeclineAsync(
        Guid id, DeclineShiftRequest r, WorkforceSchedulingDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        CommonRules.Required(errors, "Reason", r.Reason, "Reason", 500);
        errors.ThrowIfAny();

        var assignment = await FindOwnAsync(db, user, id, ct);
        assignment.Decline(r.Reason!);
        await db.SaveChangesAsync(ct);
        return (await MapAsync(db, [assignment], ct))[0];
    }

    // ------------------------------------------------------------------ swaps
    private static async Task<PagedResponse<SwapResponse>> ListSwapsAsync(
        WorkforceSchedulingDbContext db, ICurrentUser user, Guid? branchId, SwapStatus? status, int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        var branch = BranchAccess.ResolveFilter(user, branchId);
        var query = db.ShiftSwapRequests.AsNoTracking();
        if (branch is not null)
        {
            query = query.Where(s => s.BranchId == branch);
        }

        if (status is not null)
        {
            query = query.Where(s => s.Status == status);
        }

        var paged = await query.OrderByDescending(s => s.CreatedAtUtc).ToPagedAsync(page, pageSize, s => s, ct);
        return new PagedResponse<SwapResponse>(await MapSwapsAsync(db, paged.Items, ct), paged.Page, paged.PageSize, paged.TotalCount);
    }

    private static async Task<List<SwapResponse>> MySwapsAsync(WorkforceSchedulingDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var me = await EmployeeLookup.CurrentAsync(db, user, ct);
        var items = await db.ShiftSwapRequests.AsNoTracking()
            .Where(s => s.RequesterEmployeeId == me.Id || s.TargetEmployeeId == me.Id)
            .OrderByDescending(s => s.CreatedAtUtc).Take(50).ToListAsync(ct);
        return await MapSwapsAsync(db, items, ct);
    }

    private static async Task<IResult> RequestSwapAsync(
        SwapRequest r, WorkforceSchedulingDbContext db, ICurrentUser user, ShopClock shop, IConfiguration config, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        CommonRules.RequiredId(errors, "AssignmentId", r.AssignmentId, "Shift");
        CommonRules.RequiredId(errors, "TargetEmployeeId", r.TargetEmployeeId, "Colleague");
        CommonRules.Required(errors, "Reason", r.Reason, "Reason", 500);
        errors.ThrowIfAny();

        var assignment = await FindOwnAsync(db, user, r.AssignmentId!.Value, ct);
        if (!ShiftAssignment.ActiveStatuses.Contains(assignment.Status) || assignment.CheckInAtUtc is not null || assignment.WorkDate < shop.Today)
        {
            throw new BusinessRuleException("swap.not_swappable", "Only upcoming, not-yet-started shifts can be swapped.");
        }

        if (r.TargetEmployeeId == assignment.EmployeeId)
        {
            throw new ValidationFailedException("TargetEmployeeId", "Choose a colleague other than yourself.");
        }

        var target = await db.Employees.FirstOrDefaultAsync(e => e.Id == r.TargetEmployeeId, ct);
        if (target is null || target.BranchId != assignment.BranchId || target.Status != EmployeeStatus.Active)
        {
            throw new ValidationFailedException("TargetEmployeeId", "The colleague must be an active employee of the same branch.");
        }

        if (await db.ShiftSwapRequests.AnyAsync(s => s.AssignmentId == assignment.Id && s.Status == SwapStatus.Pending, ct))
        {
            throw new ConflictException("There is already a pending swap request for this shift.", "swap.already_pending");
        }

        await EnsureCanWorkAsync(db, config, target.Id, assignment.WorkDate, assignment.StartTime, assignment.EndTime, null, ct);

        var swap = new ShiftSwapRequest(assignment.BranchId, assignment.Id, assignment.EmployeeId, target.Id, r.Reason!);
        db.ShiftSwapRequests.Add(swap);
        await db.SaveChangesAsync(ct);
        return Results.Created((string?)null, (await MapSwapsAsync(db, [swap], ct))[0]);
    }

    /// <summary>BR-23: the shift moves to the colleague only now; overlap is re-checked because schedules may have changed.</summary>
    private static async Task<SwapResponse> ApproveSwapAsync(
        Guid id, DecideSwapRequest r, WorkforceSchedulingDbContext db, ICurrentUser user, ShopClock shop, IConfiguration config, CancellationToken ct)
    {
        var swap = await FindSwapAsync(db, user, id, ct);
        var assignment = await db.ShiftAssignments.FirstAsync(a => a.Id == swap.AssignmentId, ct);
        if (assignment.CheckInAtUtc is not null || assignment.EmployeeId != swap.RequesterEmployeeId)
        {
            throw new BusinessRuleException("swap.not_swappable", "The shift has started or was already reassigned.");
        }

        await EnsureCanWorkAsync(db, config, swap.TargetEmployeeId, assignment.WorkDate, assignment.StartTime, assignment.EndTime, null, ct);

        swap.Approve(user.UserId, r.Note, shop.UtcNow);
        assignment.Reassign(swap.TargetEmployeeId);
        await db.SaveChangesAsync(ct);
        return (await MapSwapsAsync(db, [swap], ct))[0];
    }

    private static async Task<SwapResponse> RejectSwapAsync(
        Guid id, DecideSwapRequest r, WorkforceSchedulingDbContext db, ICurrentUser user, ShopClock shop, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        CommonRules.Required(errors, "Note", r.Note, "Reject note", 500);
        errors.ThrowIfAny();

        var swap = await FindSwapAsync(db, user, id, ct);
        swap.Reject(user.UserId, r.Note, shop.UtcNow);
        await db.SaveChangesAsync(ct);
        return (await MapSwapsAsync(db, [swap], ct))[0];
    }

    private static async Task<SwapResponse> CancelSwapAsync(Guid id, WorkforceSchedulingDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var me = await EmployeeLookup.CurrentAsync(db, user, ct);
        var swap = await db.ShiftSwapRequests.FirstOrDefaultAsync(s => s.Id == id && s.RequesterEmployeeId == me.Id, ct)
            ?? throw new NotFoundException("Swap request", id);
        swap.Cancel();
        await db.SaveChangesAsync(ct);
        return (await MapSwapsAsync(db, [swap], ct))[0];
    }

    // ------------------------------------------------------------------ rules & helpers
    /// <summary>BR-22 (no overlap on the same date) + weekly hour cap.</summary>
    internal static async Task EnsureCanWorkAsync(
        WorkforceSchedulingDbContext db, IConfiguration config, Guid employeeId, DateOnly date, TimeOnly start, TimeOnly end,
        Guid? excludeAssignmentId, CancellationToken ct)
    {
        var weekStart = date.AddDays(-(((int)date.DayOfWeek + 6) % 7)); // Monday
        var weekEnd = weekStart.AddDays(6);
        var week = await db.ShiftAssignments.AsNoTracking()
            .Where(a => a.EmployeeId == employeeId && a.Id != excludeAssignmentId && a.WorkDate >= weekStart && a.WorkDate <= weekEnd
                        && ShiftAssignment.ActiveStatuses.Contains(a.Status))
            .ToListAsync(ct);

        if (week.Any(a => a.WorkDate == date && a.Overlaps(start, end)))
        {
            throw new ConflictException("BR-22: the employee already has a shift overlapping this time on that date.", "shift.overlap");
        }

        var maxHours = config.GetValue("Workforce:MaxWeeklyHours", 48);
        var hours = week.Sum(a => a.Hours) + (end - start).TotalHours;
        if (hours > maxHours)
        {
            throw new BusinessRuleException("shift.weekly_hours_exceeded", $"This would give the employee {hours:0.#} hours that week (max {maxHours}).");
        }
    }

    private static async Task<ShiftAssignment> FindAsync(WorkforceSchedulingDbContext db, ICurrentUser user, Guid id, CancellationToken ct)
    {
        var assignment = await db.ShiftAssignments.FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw new NotFoundException("Shift assignment", id);
        BranchAccess.EnsureCanAccess(user, assignment.BranchId);
        return assignment;
    }

    /// <summary>The caller must be the employee the shift is assigned to (BR-24: nobody acts for someone else).</summary>
    internal static async Task<ShiftAssignment> FindOwnAsync(WorkforceSchedulingDbContext db, ICurrentUser user, Guid id, CancellationToken ct)
    {
        var me = await EmployeeLookup.CurrentAsync(db, user, ct);
        return await db.ShiftAssignments.FirstOrDefaultAsync(a => a.Id == id && a.EmployeeId == me.Id, ct)
            ?? throw new NotFoundException("Shift assignment", id);
    }

    private static async Task EnsureManagerOrOwnerAsync(WorkforceSchedulingDbContext db, ICurrentUser user, ShiftAssignment assignment, CancellationToken ct)
    {
        if (user.IsInRole(AicosRoles.BrandAdmin) || user.IsInRole(AicosRoles.BranchManager))
        {
            return;
        }

        var me = await EmployeeLookup.CurrentAsync(db, user, ct);
        if (assignment.EmployeeId != me.Id)
        {
            throw new NotFoundException("Shift assignment", assignment.Id);
        }
    }

    private static async Task<ShiftSwapRequest> FindSwapAsync(WorkforceSchedulingDbContext db, ICurrentUser user, Guid id, CancellationToken ct)
    {
        var swap = await db.ShiftSwapRequests.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw new NotFoundException("Swap request", id);
        BranchAccess.EnsureCanAccess(user, swap.BranchId);
        return swap;
    }

    internal static async Task<List<ShiftAssignmentResponse>> MapAsync(WorkforceSchedulingDbContext db, IReadOnlyList<ShiftAssignment> items, CancellationToken ct)
    {
        var employeeIds = items.Select(a => a.EmployeeId).Distinct().ToList();
        var shiftIds = items.Select(a => a.WorkShiftId).Distinct().ToList();
        var names = await db.Employees.AsNoTracking().Where(e => employeeIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.FullName, ct);
        var shifts = await db.WorkShifts.AsNoTracking().Where(s => shiftIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        return items.Select(a => new ShiftAssignmentResponse(
            a.Id, a.BranchId, a.EmployeeId, names[a.EmployeeId], a.WorkShiftId, shifts[a.WorkShiftId], a.WorkDate, a.StartTime, a.EndTime,
            a.Status, a.DeclineReason, a.CheckInAtUtc, a.CheckOutAtUtc)).ToList();
    }

    private static async Task<List<SwapResponse>> MapSwapsAsync(WorkforceSchedulingDbContext db, IReadOnlyList<ShiftSwapRequest> items, CancellationToken ct)
    {
        var assignmentIds = items.Select(s => s.AssignmentId).ToList();
        var assignments = await db.ShiftAssignments.AsNoTracking().Where(a => assignmentIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        var shiftIds = assignments.Values.Select(a => a.WorkShiftId).Distinct().ToList();
        var shifts = await db.WorkShifts.AsNoTracking().Where(s => shiftIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var employeeIds = items.SelectMany(s => new[] { s.RequesterEmployeeId, s.TargetEmployeeId }).Distinct().ToList();
        var names = await db.Employees.AsNoTracking().Where(e => employeeIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.FullName, ct);

        return items.Select(s => new SwapResponse(
            s.Id, s.BranchId, s.AssignmentId, assignments[s.AssignmentId].WorkDate, shifts[assignments[s.AssignmentId].WorkShiftId],
            s.RequesterEmployeeId, names[s.RequesterEmployeeId], s.TargetEmployeeId, names[s.TargetEmployeeId], s.Reason, s.Status,
            s.ManagerNote, s.CreatedAtUtc, s.DecidedAtUtc)).ToList();
    }
}
