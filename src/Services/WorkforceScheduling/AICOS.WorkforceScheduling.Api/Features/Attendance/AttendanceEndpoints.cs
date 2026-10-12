using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Persistence;
using AICOS.BuildingBlocks.Security;
using AICOS.BuildingBlocks.Validation;
using AICOS.WorkforceScheduling.Api.Domain;
using AICOS.WorkforceScheduling.Api.Features.Scheduling;
using AICOS.WorkforceScheduling.Api.Infrastructure;
using AICOS.WorkforceScheduling.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.WorkforceScheduling.Api.Features.Attendance;

public sealed record AttendanceRequest(Guid? AssignmentId);

/// <summary>
/// UC-42 Clock In / Clock Out. Routes: /api/v1/workforce/attendance.
/// BR-24: only for your own assigned shift, from 15 minutes before it starts until it ends.
/// BR-46: two attendance actions of the same employee must be at least 5 minutes apart (no double taps).
/// </summary>
public static class AttendanceEndpoints
{
    public const int EarlyCheckInMinutes = 15;
    public const int MinIntervalMinutes = 5;

    public static void MapAttendanceEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/attendance").WithTags("Attendance");

        group.MapPost("/check-in", CheckInAsync).RequireAuthorization(AicosPolicies.Staff);
        group.MapPost("/check-out", CheckOutAsync).RequireAuthorization(AicosPolicies.Staff);
        group.MapGet("/", ListAsync).RequireAuthorization(AicosPolicies.Management);
    }

    private static async Task<ShiftAssignmentResponse> CheckInAsync(
        AttendanceRequest r, WorkforceSchedulingDbContext db, ICurrentUser user, ShopClock shop, CancellationToken ct)
    {
        var assignment = await FindOwnAsync(db, user, r, ct);
        if (!ShiftAssignment.ActiveStatuses.Contains(assignment.Status))
        {
            throw new BusinessRuleException("BR-24", "You declined this shift and cannot check in for it.");
        }

        if (assignment.CheckInAtUtc is not null)
        {
            throw new ConflictException("You already checked in for this shift.", "attendance.already_checked_in");
        }

        var now = shop.UtcNow;
        var opens = shop.ToUtc(assignment.WorkDate, assignment.StartTime).AddMinutes(-EarlyCheckInMinutes);
        var closes = shop.ToUtc(assignment.WorkDate, assignment.EndTime);
        if (now < opens || now >= closes)
        {
            throw new BusinessRuleException(
                "BR-24",
                $"Check-in is only possible from {EarlyCheckInMinutes} minutes before the shift starts until it ends " +
                $"({assignment.WorkDate:yyyy-MM-dd} {assignment.StartTime:HH\\:mm}-{assignment.EndTime:HH\\:mm}).");
        }

        await EnsureMinIntervalAsync(db, assignment.EmployeeId, now, ct);
        assignment.CheckIn(now);
        await db.SaveChangesAsync(ct);
        return (await ScheduleEndpoints.MapAsync(db, [assignment], ct))[0];
    }

    private static async Task<ShiftAssignmentResponse> CheckOutAsync(
        AttendanceRequest r, WorkforceSchedulingDbContext db, ICurrentUser user, ShopClock shop, CancellationToken ct)
    {
        var assignment = await FindOwnAsync(db, user, r, ct);
        if (assignment.CheckInAtUtc is null)
        {
            throw new BusinessRuleException("attendance.not_checked_in", "You have not checked in for this shift.");
        }

        if (assignment.CheckOutAtUtc is not null)
        {
            throw new ConflictException("You already checked out of this shift.", "attendance.already_checked_out");
        }

        var now = shop.UtcNow;
        await EnsureMinIntervalAsync(db, assignment.EmployeeId, now, ct);
        assignment.CheckOut(now);
        await db.SaveChangesAsync(ct);
        return (await ScheduleEndpoints.MapAsync(db, [assignment], ct))[0];
    }

    /// <summary>Manager view: shifts in a date range with their check-in/out times (input for payroll / UC-40).</summary>
    private static async Task<PagedResponse<ShiftAssignmentResponse>> ListAsync(
        WorkforceSchedulingDbContext db, ICurrentUser user, ShopClock shop, Guid? branchId, DateOnly? from, DateOnly? to, Guid? employeeId,
        int page = 1, int pageSize = 50, CancellationToken ct = default)
    {
        var branch = BranchAccess.ResolveFilter(user, branchId);
        var start = from ?? shop.Today.AddDays(-6);
        var end = to ?? shop.Today;
        var query = db.ShiftAssignments.AsNoTracking().Where(a => a.WorkDate >= start && a.WorkDate <= end);
        if (branch is not null)
        {
            query = query.Where(a => a.BranchId == branch);
        }

        if (employeeId is not null)
        {
            query = query.Where(a => a.EmployeeId == employeeId);
        }

        var paged = await query.OrderBy(a => a.WorkDate).ThenBy(a => a.StartTime).ToPagedAsync(page, pageSize, a => a, ct);
        return new PagedResponse<ShiftAssignmentResponse>(await ScheduleEndpoints.MapAsync(db, paged.Items, ct), paged.Page, paged.PageSize, paged.TotalCount);
    }

    private static async Task<ShiftAssignment> FindOwnAsync(WorkforceSchedulingDbContext db, ICurrentUser user, AttendanceRequest r, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        CommonRules.RequiredId(errors, "AssignmentId", r.AssignmentId, "Shift");
        errors.ThrowIfAny();
        return await ScheduleEndpoints.FindOwnAsync(db, user, r.AssignmentId!.Value, ct);
    }

    /// <summary>BR-46.</summary>
    private static async Task EnsureMinIntervalAsync(WorkforceSchedulingDbContext db, Guid employeeId, DateTime nowUtc, CancellationToken ct)
    {
        var since = nowUtc.AddMinutes(-MinIntervalMinutes);
        var recent = await db.ShiftAssignments.AnyAsync(a => a.EmployeeId == employeeId
                                                             && ((a.CheckInAtUtc != null && a.CheckInAtUtc > since)
                                                                 || (a.CheckOutAtUtc != null && a.CheckOutAtUtc > since)), ct);
        if (recent)
        {
            throw new BusinessRuleException("BR-46", $"Attendance actions must be at least {MinIntervalMinutes} minutes apart.");
        }
    }
}
