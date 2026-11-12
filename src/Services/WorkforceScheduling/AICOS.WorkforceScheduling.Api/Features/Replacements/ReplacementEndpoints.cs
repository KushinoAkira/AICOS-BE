using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Security;
using AICOS.BuildingBlocks.Validation;
using AICOS.Contracts.Events;
using AICOS.WorkforceScheduling.Api.Domain;
using AICOS.WorkforceScheduling.Api.Features.Employees;
using AICOS.WorkforceScheduling.Api.Features.Scheduling;
using AICOS.WorkforceScheduling.Api.Infrastructure;
using AICOS.WorkforceScheduling.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.WorkforceScheduling.Api.Features.Replacements;

/// <summary>
/// Open a replacement shift. Give <see cref="AssignmentId"/> to replace an existing assignment (the employee is absent or declined),
/// or <see cref="WorkShiftId"/> + <see cref="WorkDate"/> for an extra shift nobody was assigned to.
/// </summary>
public sealed record OpenReplacementRequest(Guid? AssignmentId, Guid? WorkShiftId, DateOnly? WorkDate, string? Reason);

public sealed record RegisterReplacementRequest(string? Note);

public sealed record ApproveReplacementRequest(Guid? ApplicationId, string? Note);

public sealed record CancelReplacementRequest(string? Note);

public sealed record ReplacementApplicationResponse(
    Guid Id, Guid EmployeeId, string EmployeeName, ContractType ContractType, ApplicationStatus Status, string? Note, DateTime CreatedAtUtc);

public sealed record ReplacementShiftResponse(
    Guid Id,
    Guid BranchId,
    DateOnly WorkDate,
    TimeOnly StartTime,
    TimeOnly EndTime,
    double Hours,
    Guid? WorkShiftId,
    string ShiftName,
    Guid? OriginalAssignmentId,
    string? OriginalEmployeeName,
    string Reason,
    ReplacementStatus Status,
    Guid? ApprovedEmployeeId,
    string? ApprovedEmployeeName,
    Guid? ApprovedAssignmentId,
    string? ManagerNote,
    int Applications,
    ApplicationStatus? MyApplication,
    DateTime CreatedAtUtc);

/// <summary>
/// UC-30 Open and Approve Replacement Shift, UC-31 Register for Replacement Shift. Routes: /api/v1/workforce/replacement-shifts.
/// The Branch Manager opens a shift that needs somebody else, or a Barista or Cashier asks for a replacement of their own coming shift;
/// Baristas and Cashiers of the same branch register; the replacement
/// only takes effect when the Branch Manager approves one of them (BR-51). The replacement works it as a normal assignment (check-in,
/// check-out, review), is paid at the brand replacement rate, and keeps their contract type (BR-52). Approval is audited.
/// </summary>
public static class ReplacementEndpoints
{
    public static void MapReplacementEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/replacement-shifts").WithTags("Replacement shifts");

        group.MapGet("/", ListAsync).RequireAuthorization(AicosPolicies.Staff);
        group.MapGet("/{id:guid}", GetAsync).RequireAuthorization(AicosPolicies.Staff).WithName("GetReplacementShift");
        group.MapPost("/", OpenAsync).RequireAuthorization(AicosPolicies.Staff);
        group.MapGet("/{id:guid}/applications", ListApplicationsAsync).RequireAuthorization(AicosPolicies.Management);
        group.MapPost("/{id:guid}/approve", ApproveAsync).RequireAuthorization(AicosPolicies.Management);
        group.MapPost("/{id:guid}/cancel", CancelAsync).RequireAuthorization(AicosPolicies.Management);
        group.MapPost("/{id:guid}/register", RegisterAsync).RequireAuthorization(AicosPolicies.Staff);
        group.MapPost("/{id:guid}/withdraw", WithdrawAsync).RequireAuthorization(AicosPolicies.Staff);
        group.MapPost("/{id:guid}/retract", RetractAsync).RequireAuthorization(AicosPolicies.Staff);
    }

    // ------------------------------------------------------------------ list / get
    /// <summary>Managers see every replacement shift of their scope; staff see the ones of their own branch (open ones by default).</summary>
    private static async Task<List<ReplacementShiftResponse>> ListAsync(
        WorkforceSchedulingDbContext db, ICurrentUser user, IBranchDirectory directory, Guid? branchId, ReplacementStatus? status, DateOnly? from, DateOnly? to,
        CancellationToken ct)
    {
        var tenantId = TenantScope.Require(user);
        var isManager = user.IsInRole(AicosRoles.BrandAdmin) || user.IsInRole(AicosRoles.BranchManager);
        var branch = await WorkforceScope.FilterAsync(user, directory, branchId, ct);

        var query = db.ReplacementShifts.AsNoTracking().Where(r => r.TenantId == tenantId);
        if (branch is not null)
        {
            query = query.Where(r => r.BranchId == branch);
        }

        var effectiveStatus = status ?? (isManager ? null : ReplacementStatus.Open);
        if (effectiveStatus is not null)
        {
            query = query.Where(r => r.Status == effectiveStatus);
        }

        if (from is not null)
        {
            query = query.Where(r => r.WorkDate >= from);
        }

        if (to is not null)
        {
            query = query.Where(r => r.WorkDate <= to);
        }

        var items = await query.OrderBy(r => r.WorkDate).ThenBy(r => r.StartTime).Take(200).ToListAsync(ct);
        return await MapAsync(db, user, items, ct);
    }

    private static async Task<ReplacementShiftResponse> GetAsync(Guid id, WorkforceSchedulingDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var replacement = await FindAsync(db, user, id, ct);
        return (await MapAsync(db, user, [replacement], ct))[0];
    }

    // ------------------------------------------------------------------ manager: open / approve / cancel
    private static async Task<IResult> OpenAsync(
        OpenReplacementRequest r, WorkforceSchedulingDbContext db, ICurrentUser user, IBranchDirectory directory, ShopClock shop, IEventBus bus, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        CommonRules.Required(errors, "Reason", r.Reason, "Reason", 500);
        if (r.AssignmentId is null && (r.WorkShiftId is null || r.WorkDate is null))
        {
            errors.Add("AssignmentId", "Give the assignment to replace, or a work shift and a work date.");
        }

        if (r.AssignmentId is not null && (r.WorkShiftId is not null || r.WorkDate is not null))
        {
            errors.Add("AssignmentId", "Give either the assignment to replace, or a work shift and a work date, not both.");
        }

        errors.ThrowIfAny();

        var tenantId = TenantScope.Require(user);
        var isManager = user.IsInRole(AicosRoles.BrandAdmin) || user.IsInRole(AicosRoles.BranchManager);
        if (!isManager && r.AssignmentId is null)
        {
            throw new ForbiddenException("Staff can only ask for a replacement of their own shift.", "replacement.own_shift_only");
        }

        ReplacementShift replacement;
        if (r.AssignmentId is not null)
        {
            // Staff ask for their own shift only (BR-49); the manager still picks the volunteer (BR-51).
            var original = isManager
                ? await ScheduleEndpoints.FindAsync(db, user, r.AssignmentId.Value, ct)
                : await ScheduleEndpoints.FindOwnAsync(db, user, r.AssignmentId.Value, ct);
            if (original.Status == AssignmentStatus.Replaced)
            {
                throw new BusinessRuleException("replacement.already_replaced", "This shift already has a replacement.");
            }

            if (original.CheckInAtUtc is not null || original.ReviewStatus == AttendanceReviewStatus.Approved)
            {
                throw new BusinessRuleException("replacement.shift_worked", "The employee already worked this shift; it cannot be replaced.");
            }

            EnsureNotOver(original.WorkDate, original.EndTime, shop);
            if (await db.ReplacementShifts.AnyAsync(x => x.OriginalAssignmentId == original.Id && x.Status != ReplacementStatus.Cancelled, ct))
            {
                throw new ConflictException("A replacement shift is already open for this assignment.", "replacement.duplicate");
            }

            replacement = new ReplacementShift(
                tenantId, original.BranchId, original.WorkShiftId, original.WorkDate, original.StartTime, original.EndTime, original.Id, r.Reason!);
        }
        else
        {
            var template = await EmployeeEndpoints.FindShiftAsync(db, user, r.WorkShiftId!.Value, ct);
            if (!template.IsActive)
            {
                throw new ValidationFailedException("WorkShiftId", "The shift template is inactive.");
            }

            await BranchScope.ResolveAsync(user, directory, template.BranchId, requireActive: true, ct);
            EnsureNotOver(r.WorkDate!.Value, template.EndTime, shop);
            if (r.WorkDate > shop.Today.AddDays(ScheduleEndpoints.MaxDaysAhead))
            {
                throw new ValidationFailedException("WorkDate", $"Shifts can be opened at most {ScheduleEndpoints.MaxDaysAhead} days ahead.");
            }

            replacement = new ReplacementShift(tenantId, template.BranchId, template.Id, r.WorkDate!.Value, template.StartTime, template.EndTime, null, r.Reason!);
        }

        db.ReplacementShifts.Add(replacement);
        await db.SaveChangesAsync(ct);
        await bus.PublishAsync(new ReplacementShiftOpenedEvent(replacement.TenantId, replacement.BranchId, replacement.Id, replacement.WorkDate, replacement.StartTime, replacement.EndTime), ct);
        return Results.CreatedAtRoute("GetReplacementShift", new { id = replacement.Id }, (await MapAsync(db, user, [replacement], ct))[0]);
    }

    private static async Task<List<ReplacementApplicationResponse>> ListApplicationsAsync(
        Guid id, WorkforceSchedulingDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var replacement = await FindAsync(db, user, id, ct);
        var applications = await db.ReplacementApplications.AsNoTracking().Where(a => a.ReplacementShiftId == replacement.Id).OrderBy(a => a.CreatedAtUtc).ToListAsync(ct);
        var ids = applications.Select(a => a.EmployeeId).ToList();
        var employees = await db.Employees.AsNoTracking().Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct);
        return applications.Select(a => new ReplacementApplicationResponse(
            a.Id, a.EmployeeId, employees[a.EmployeeId].FullName, employees[a.EmployeeId].ContractType, a.Status, a.Note, a.CreatedAtUtc)).ToList();
    }

    /// <summary>BR-51: the Branch Manager picks one volunteer. Only then does the replacement get a shift, and the original one is released.</summary>
    private static async Task<ReplacementShiftResponse> ApproveAsync(
        Guid id,
        ApproveReplacementRequest r,
        WorkforceSchedulingDbContext db,
        ICurrentUser user,
        ShopClock shop,
        IConfiguration config,
        IEventBus bus,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var errors = new ValidationErrors();
        CommonRules.RequiredId(errors, "ApplicationId", r.ApplicationId, "Application");
        CommonRules.MaxLength(errors, "Note", r.Note, "Note", 500);
        errors.ThrowIfAny();

        var replacement = await FindAsync(db, user, id, ct);
        if (replacement.Status != ReplacementStatus.Open)
        {
            throw new BusinessRuleException("replacement.not_open", $"A replacement shift in status {replacement.Status} can no longer be changed.");
        }

        var application = await db.ReplacementApplications.FirstOrDefaultAsync(a => a.Id == r.ApplicationId && a.ReplacementShiftId == id, ct)
            ?? throw new NotFoundException("Application", r.ApplicationId!);
        if (application.Status != ApplicationStatus.Pending)
        {
            throw new BusinessRuleException("replacement.application_closed", $"An application in status {application.Status} cannot be approved.");
        }

        EnsureNotOver(replacement.WorkDate, replacement.EndTime, shop);
        var employee = await db.Employees.FirstAsync(e => e.Id == application.EmployeeId, ct);
        if (employee.Status != EmployeeStatus.Active)
        {
            throw new BusinessRuleException("replacement.employee_inactive", "The employee is inactive.");
        }

        // The employee may have been scheduled elsewhere since registering.
        await ScheduleEndpoints.EnsureCanWorkAsync(db, config, employee.Id, replacement.WorkDate, replacement.StartTime, replacement.EndTime, null, ct);

        var now = shop.UtcNow;
        var assignment = new ShiftAssignment(
            replacement.TenantId, replacement.BranchId, employee.Id, replacement.WorkShiftId, replacement.WorkDate, replacement.StartTime, replacement.EndTime, replacement.Id);
        db.ShiftAssignments.Add(assignment);

        ShiftAssignment? original = null;
        if (replacement.OriginalAssignmentId is { } originalId)
        {
            original = await db.ShiftAssignments.FirstOrDefaultAsync(a => a.Id == originalId, ct);
            if (original is { Status: AssignmentStatus.Assigned or AssignmentStatus.Confirmed })
            {
                original.MarkReplaced();
            }
        }

        application.Approve();
        foreach (var other in await db.ReplacementApplications.Where(a => a.ReplacementShiftId == id && a.Id != application.Id && a.Status == ApplicationStatus.Pending).ToListAsync(ct))
        {
            other.Reject();
        }

        replacement.Fill(employee.Id, assignment.Id, r.Note, user.UserId, now);
        await db.SaveChangesAsync(ct);

        await AuditTrail.PublishAsync(
            bus, user, loggers.CreateLogger("AICOS.Workforce.Replacement"), replacement.TenantId, "shift.replacement_approved", "ReplacementShift", replacement.Id,
            before: new { Status = ReplacementStatus.Open.ToString(), replacement.OriginalAssignmentId },
            after: new
            {
                Status = replacement.Status.ToString(), replacement.WorkDate, replacement.StartTime, replacement.EndTime,
                ReplacementEmployeeId = employee.Id, ReplacementEmployee = employee.FullName, replacement.ApprovedAssignmentId,
            });
        return (await MapAsync(db, user, [replacement], ct))[0];
    }

    private static async Task<ReplacementShiftResponse> CancelAsync(
        Guid id, CancelReplacementRequest r, WorkforceSchedulingDbContext db, ICurrentUser user, ShopClock shop, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        CommonRules.MaxLength(errors, "Note", r.Note, "Note", 500);
        errors.ThrowIfAny();

        var replacement = await FindAsync(db, user, id, ct);
        replacement.Cancel(r.Note, user.UserId, shop.UtcNow);
        foreach (var pending in await db.ReplacementApplications.Where(a => a.ReplacementShiftId == id && a.Status == ApplicationStatus.Pending).ToListAsync(ct))
        {
            pending.Reject();
        }

        await db.SaveChangesAsync(ct);
        return (await MapAsync(db, user, [replacement], ct))[0];
    }

    // ------------------------------------------------------------------ employee: register / withdraw
    private static async Task<ReplacementShiftResponse> RegisterAsync(
        Guid id, RegisterReplacementRequest r, WorkforceSchedulingDbContext db, ICurrentUser user, ShopClock shop, IConfiguration config, IEventBus bus, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        CommonRules.MaxLength(errors, "Note", r.Note, "Note", 500);
        errors.ThrowIfAny();

        var me = await EmployeeLookup.CurrentAsync(db, user, ct);
        var replacement = await FindAsync(db, user, id, ct);
        if (replacement.Status != ReplacementStatus.Open)
        {
            throw new BusinessRuleException("replacement.not_open", "This replacement shift is no longer open.");
        }

        if (me.BranchId != replacement.BranchId || me.Status != EmployeeStatus.Active)
        {
            throw new NotFoundException("Replacement shift", id); // staff only see the open shifts of their own branch
        }

        EnsureNotOver(replacement.WorkDate, replacement.EndTime, shop);
        if (replacement.OriginalAssignmentId is { } originalId
            && await db.ShiftAssignments.AnyAsync(a => a.Id == originalId && a.EmployeeId == me.Id, ct))
        {
            throw new BusinessRuleException("replacement.own_shift", "You cannot replace your own shift.");
        }

        if (await db.ReplacementApplications.AnyAsync(a => a.ReplacementShiftId == id && a.EmployeeId == me.Id && a.Status == ApplicationStatus.Pending, ct))
        {
            throw new ConflictException("You already registered for this shift.", "replacement.already_registered");
        }

        await ScheduleEndpoints.EnsureCanWorkAsync(db, config, me.Id, replacement.WorkDate, replacement.StartTime, replacement.EndTime, null, ct);

        db.ReplacementApplications.Add(new ReplacementApplication(id, me.Id, r.Note));
        await db.SaveChangesAsync(ct);
        await bus.PublishAsync(new ReplacementRegisteredEvent(replacement.TenantId, replacement.BranchId, replacement.Id, me.Id, me.FullName, replacement.WorkDate), ct);
        return (await MapAsync(db, user, [replacement], ct))[0];
    }

    private static async Task<ReplacementShiftResponse> WithdrawAsync(Guid id, WorkforceSchedulingDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var me = await EmployeeLookup.CurrentAsync(db, user, ct);
        var replacement = await FindAsync(db, user, id, ct);
        var application = await db.ReplacementApplications.FirstOrDefaultAsync(a => a.ReplacementShiftId == id && a.EmployeeId == me.Id && a.Status == ApplicationStatus.Pending, ct)
            ?? throw new NotFoundException("Your registration for the replacement shift", id);

        application.Withdraw();
        await db.SaveChangesAsync(ct);
        return (await MapAsync(db, user, [replacement], ct))[0];
    }

    /// <summary>
    /// The employee who asked for somebody to take their shift takes the request back (no longer busy). Only while nobody was approved:
    /// the request is cancelled, the colleagues' sign-ups are rejected and the shift simply stays theirs.
    /// </summary>
    private static async Task<ReplacementShiftResponse> RetractAsync(Guid id, WorkforceSchedulingDbContext db, ICurrentUser user, ShopClock shop, CancellationToken ct)
    {
        var replacement = await FindAsync(db, user, id, ct);
        var me = await EmployeeLookup.CurrentAsync(db, user, ct);
        var isMine = replacement.OriginalAssignmentId is not null
            && await db.ShiftAssignments.AnyAsync(a => a.Id == replacement.OriginalAssignmentId && a.EmployeeId == me.Id, ct);
        if (!isMine)
        {
            throw new ForbiddenException("Only the employee who asked for the replacement can take it back.", "replacement.not_requester");
        }

        if (replacement.Status != ReplacementStatus.Open)
        {
            throw new BusinessRuleException("replacement.not_open", "This replacement shift is no longer open.");
        }

        replacement.Cancel("Taken back by the employee.", user.UserId, shop.UtcNow);
        foreach (var pending in await db.ReplacementApplications.Where(a => a.ReplacementShiftId == id && a.Status == ApplicationStatus.Pending).ToListAsync(ct))
        {
            pending.Reject();
        }

        await db.SaveChangesAsync(ct);
        return (await MapAsync(db, user, [replacement], ct))[0];
    }

    // ------------------------------------------------------------------ helpers
    private static void EnsureNotOver(DateOnly workDate, TimeOnly endTime, ShopClock shop)
    {
        if (shop.ToUtc(workDate, endTime) <= shop.UtcNow)
        {
            throw new BusinessRuleException("replacement.shift_over", "That shift is already over.");
        }
    }

    /// <summary>Another brand replacement shift is not found (BR-04); staff of another branch are forbidden, managers of another branch too (BR-05).</summary>
    private static async Task<ReplacementShift> FindAsync(WorkforceSchedulingDbContext db, ICurrentUser user, Guid id, CancellationToken ct)
    {
        var tenantId = TenantScope.Require(user);
        var replacement = await db.ReplacementShifts.FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId, ct)
            ?? throw new NotFoundException("Replacement shift", id);
        BranchAccess.EnsureCanAccess(user, replacement.BranchId);
        return replacement;
    }

    private static async Task<List<ReplacementShiftResponse>> MapAsync(
        WorkforceSchedulingDbContext db, ICurrentUser user, IReadOnlyList<ReplacementShift> items, CancellationToken ct)
    {
        var ids = items.Select(i => i.Id).ToList();
        var shiftIds = items.Where(i => i.WorkShiftId != null).Select(i => i.WorkShiftId!.Value).Distinct().ToList();
        var originalIds = items.Where(i => i.OriginalAssignmentId != null).Select(i => i.OriginalAssignmentId!.Value).ToList();

        var shifts = await db.WorkShifts.AsNoTracking().Where(s => shiftIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var originals = await db.ShiftAssignments.AsNoTracking().Where(a => originalIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.EmployeeId, ct);
        var employeeIds = originals.Values.Concat(items.Where(i => i.ApprovedEmployeeId != null).Select(i => i.ApprovedEmployeeId!.Value)).Distinct().ToList();
        var names = await db.Employees.AsNoTracking().Where(e => employeeIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.FullName, ct);
        var counts = await db.ReplacementApplications.AsNoTracking()
            .Where(a => ids.Contains(a.ReplacementShiftId) && a.Status != ApplicationStatus.Withdrawn)
            .GroupBy(a => a.ReplacementShiftId)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count, ct);

        var myId = user.IsInRole(AicosRoles.Barista) || user.IsInRole(AicosRoles.Cashier)
            ? (await EmployeeLookup.CurrentAsync(db, user, ct)).Id
            : (Guid?)null;
        var mine = myId is null
            ? []
            : await db.ReplacementApplications.AsNoTracking()
                .Where(a => ids.Contains(a.ReplacementShiftId) && a.EmployeeId == myId)
                .GroupBy(a => a.ReplacementShiftId)
                .Select(g => new { Id = g.Key, Status = g.OrderByDescending(a => a.CreatedAtUtc).First().Status })
                .ToDictionaryAsync(x => x.Id, x => x.Status, ct);

        return items.Select(i => new ReplacementShiftResponse(
            i.Id, i.BranchId, i.WorkDate, i.StartTime, i.EndTime, i.Hours, i.WorkShiftId,
            i.WorkShiftId is { } sid ? shifts.GetValueOrDefault(sid, "Shift") : "Extra shift",
            i.OriginalAssignmentId,
            i.OriginalAssignmentId is { } oid && originals.TryGetValue(oid, out var originalEmployee) ? names.GetValueOrDefault(originalEmployee) : null,
            i.Reason, i.Status, i.ApprovedEmployeeId, i.ApprovedEmployeeId is { } eid ? names.GetValueOrDefault(eid) : null, i.ApprovedAssignmentId,
            i.ManagerNote, counts.GetValueOrDefault(i.Id), mine.TryGetValue(i.Id, out var status) ? status : null, i.CreatedAtUtc)).ToList();
    }
}
