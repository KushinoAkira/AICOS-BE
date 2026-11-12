using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Security;
using AICOS.BuildingBlocks.Validation;
using AICOS.WorkforceScheduling.Api.Domain;
using AICOS.WorkforceScheduling.Api.Features.Scheduling;
using AICOS.WorkforceScheduling.Api.Infrastructure;
using AICOS.WorkforceScheduling.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.WorkforceScheduling.Api.Features.Attendance;

public sealed record AttendanceRequest(Guid? AssignmentId);

public sealed record ApproveAttendanceRequest(string? Note);

/// <summary>Correct the recorded times (either or both, UTC) with a reason. The corrected record is approved at once.</summary>
public sealed record AdjustAttendanceRequest(DateTimeOffset? CheckInAt, DateTimeOffset? CheckOutAt, string? Reason);

public sealed record MarkAbsentRequest(string? Reason);

public sealed record ApproveRangeRequest(Guid? BranchId, DateOnly? From, DateOnly? To, Guid? EmployeeId);

public sealed record ApproveRangeResult(int Approved, int NeedsAttention);

/// <summary>Where a shift stands once attendance is compared with the schedule (UC-29).</summary>
public enum AttendanceState
{
    Upcoming,
    InProgress,
    Worked,

    /// <summary>The shift ended with a check-in but no check-out.</summary>
    MissingCheckOut,

    /// <summary>The shift ended and the employee never checked in.</summary>
    NoShow,

    /// <summary>The Branch Manager recorded the employee as absent.</summary>
    Absent,

    Declined,
    Replaced,
}

public sealed record AttendanceRow(
    ShiftAssignmentResponse Shift, AttendanceState State, bool IsLate, int LateMinutes, bool LeftEarly, double ScheduledHours, double WorkedHours);

public sealed record AttendanceSummary(
    Guid EmployeeId,
    string EmployeeName,
    ContractType ContractType,
    int Shifts,
    double ScheduledHours,
    double WorkedHours,
    int LateCount,
    int Absences,
    int NoShows,
    int PendingReview);

public sealed record AttendanceReportResponse(DateOnly From, DateOnly To, List<AttendanceRow> Rows, List<AttendanceSummary> Summary);

/// <summary>
/// UC-48 Check In / Check Out, UC-29 Review Attendance Records. Routes: /api/v1/workforce/attendance.
/// BR-49: only for your own assigned shift, from 15 minutes before it starts until it ends. BR-50: two attendance actions of the
/// same employee must be at least 5 minutes apart. BR-54 / FR-82: a record is only used for payroll after the Branch Manager approved it
/// (as recorded, corrected, or as an absence); corrections and absences are kept in the audit history.
/// </summary>
public static class AttendanceEndpoints
{
    private const string ClosedOnApprovalNote = "Duyệt trong ca, giờ ra tính đến lúc duyệt";

    public const int EarlyCheckInMinutes = 15;
    public const int MinIntervalMinutes = 5;
    public const int MaxReportDays = 62;

    public static void MapAttendanceEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/attendance").WithTags("Attendance");

        group.MapPost("/check-in", CheckInAsync).RequireAuthorization(AicosPolicies.Staff);
        group.MapPost("/check-out", CheckOutAsync).RequireAuthorization(AicosPolicies.Staff);

        group.MapGet("/report", ReportAsync).RequireAuthorization(AicosPolicies.Management);
        group.MapPost("/approve-range", ApproveRangeAsync).RequireAuthorization(AicosPolicies.Management);
        group.MapPost("/{id:guid}/approve", ApproveAsync).RequireAuthorization(AicosPolicies.Management);
        group.MapPost("/{id:guid}/adjust", AdjustAsync).RequireAuthorization(AicosPolicies.Management);
        group.MapPost("/{id:guid}/absent", AbsentAsync).RequireAuthorization(AicosPolicies.Management);
    }

    // ------------------------------------------------------------------ employee: check in / out
    private static async Task<ShiftAssignmentResponse> CheckInAsync(
        AttendanceRequest r, WorkforceSchedulingDbContext db, ICurrentUser user, ShopClock shop, IEventBus bus, ILoggerFactory loggers, CancellationToken ct)
    {
        var assignment = await FindOwnAsync(db, user, r, ct);
        if (!ShiftAssignment.ActiveStatuses.Contains(assignment.Status))
        {
            throw new BusinessRuleException("attendance.shift_not_active", $"This shift is {assignment.Status} and cannot be checked in for.");
        }

        if (assignment.CheckInAtUtc is not null)
        {
            throw new ConflictException("You already checked in for this shift.", "attendance.already_checked_in");
        }

        EnsureNotReviewed(assignment);

        var now = shop.UtcNow;
        var opens = shop.ToUtc(assignment.WorkDate, assignment.StartTime).AddMinutes(-EarlyCheckInMinutes);
        var closes = shop.ToUtc(assignment.WorkDate, assignment.EndTime);
        if (now < opens || now >= closes)
        {
            throw new BusinessRuleException(
                "attendance.outside_window",
                $"Check-in is only possible from {EarlyCheckInMinutes} minutes before the shift starts until it ends " +
                $"({assignment.WorkDate:yyyy-MM-dd} {assignment.StartTime:HH\\:mm}-{assignment.EndTime:HH\\:mm}).");
        }

        await EnsureMinIntervalAsync(db, assignment.EmployeeId, now, ct);
        assignment.CheckIn(now);
        await db.SaveChangesAsync(ct);
        await AuditTrail.PublishAsync(bus, user, loggers.CreateLogger("AICOS.Workforce.Attendance"), assignment.TenantId, "attendance.checked_in", "ShiftAssignment", assignment.Id, null, new { assignment.EmployeeId, assignment.WorkDate, CheckInAtUtc = now });
        return (await ScheduleEndpoints.MapAsync(db, [assignment], ct))[0];
    }

    private static async Task<ShiftAssignmentResponse> CheckOutAsync(
        AttendanceRequest r, WorkforceSchedulingDbContext db, ICurrentUser user, ShopClock shop, IEventBus bus, ILoggerFactory loggers, CancellationToken ct)
    {
        var assignment = await FindOwnAsync(db, user, r, ct);
        if (assignment.CheckOutAtUtc is not null)
        {
            throw new ConflictException("You already checked out of this shift.", "attendance.already_checked_out");
        }

        EnsureNotReviewed(assignment);
        if (assignment.CheckInAtUtc is null)
        {
            throw new BusinessRuleException("attendance.not_checked_in", "You have not checked in for this shift.");
        }

        var now = shop.UtcNow;
        await EnsureMinIntervalAsync(db, assignment.EmployeeId, now, ct);
        assignment.CheckOut(now);
        await db.SaveChangesAsync(ct);
        await AuditTrail.PublishAsync(bus, user, loggers.CreateLogger("AICOS.Workforce.Attendance"), assignment.TenantId, "attendance.checked_out", "ShiftAssignment", assignment.Id, null, new { assignment.EmployeeId, assignment.WorkDate, CheckOutAtUtc = now });
        return (await ScheduleEndpoints.MapAsync(db, [assignment], ct))[0];
    }

    // ------------------------------------------------------------------ manager: review
    /// <summary>
    /// UC-29: the schedule compared with attendance for a period (at most 62 days): per shift, and per employee. Late and early
    /// leaving use <c>Workforce:LateGraceMinutes</c> (default 5).
    /// </summary>
    private static async Task<AttendanceReportResponse> ReportAsync(
        WorkforceSchedulingDbContext db, ICurrentUser user, IBranchDirectory directory, ShopClock shop, IConfiguration config,
        Guid? branchId, DateOnly? from, DateOnly? to, Guid? employeeId, AttendanceReviewStatus? reviewStatus, CancellationToken ct)
    {
        var start = from ?? shop.Today.AddDays(-6);
        var end = to ?? shop.Today;
        if (start > end)
        {
            throw new ValidationFailedException("from", "'from' must not be later than 'to'.");
        }

        if (end.DayNumber - start.DayNumber >= MaxReportDays)
        {
            throw new ValidationFailedException("from", $"The report covers at most {MaxReportDays} days.");
        }

        var tenantId = TenantScope.Require(user);
        var branch = await WorkforceScope.FilterAsync(user, directory, branchId, ct);
        var query = db.ShiftAssignments.AsNoTracking().Where(a => a.TenantId == tenantId && a.WorkDate >= start && a.WorkDate <= end);
        if (branch is not null)
        {
            query = query.Where(a => a.BranchId == branch);
        }

        if (employeeId is not null)
        {
            query = query.Where(a => a.EmployeeId == employeeId);
        }

        if (reviewStatus is not null)
        {
            query = query.Where(a => a.ReviewStatus == reviewStatus);
        }

        var assignments = await query.OrderBy(a => a.WorkDate).ThenBy(a => a.StartTime).ToListAsync(ct);
        var mapped = await ScheduleEndpoints.MapAsync(db, assignments, ct);
        var grace = GraceMinutes(config);
        var now = shop.UtcNow;

        var rows = assignments.Select((a, i) =>
        {
            var (late, lateMinutes, early) = Punctuality(a, shop, grace);
            return new AttendanceRow(mapped[i], StateOf(a, shop, now, grace), late, lateMinutes, early, Math.Round(a.Hours, 2), a.WorkedHours);
        }).ToList();

        var employeeIds = assignments.Select(a => a.EmployeeId).Distinct().ToList();
        var employees = await db.Employees.AsNoTracking()
            .Where(e => employeeIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, ct);

        var summary = rows
            .GroupBy(r => r.Shift.EmployeeId)
            .Select(g => new AttendanceSummary(
                g.Key,
                employees[g.Key].FullName,
                employees[g.Key].ContractType,
                g.Count(r => r.State is not (AttendanceState.Declined or AttendanceState.Replaced)),
                g.Where(r => r.State is not (AttendanceState.Declined or AttendanceState.Replaced)).Sum(r => r.ScheduledHours),
                Math.Round(g.Sum(r => r.WorkedHours), 2),
                g.Count(r => r.IsLate),
                g.Count(r => r.State == AttendanceState.Absent),
                g.Count(r => r.State == AttendanceState.NoShow),
                g.Count(r => r.Shift.ReviewStatus == AttendanceReviewStatus.Unreviewed && r.State is not (AttendanceState.Declined or AttendanceState.Replaced or AttendanceState.Upcoming))))
            .OrderBy(s => s.EmployeeName)
            .ToList();

        return new AttendanceReportResponse(start, end, rows, summary);
    }

    /// <summary>
    /// UC-29: approve what was recorded. The manager need not wait for the end of the shift: once the employee has checked in the record can be
    /// approved, and a shift still running is closed at this moment, because payroll counts hours up to a check-out. That close is audited.
    /// </summary>
    private static async Task<ShiftAssignmentResponse> ApproveAsync(
        Guid id, ApproveAttendanceRequest r, WorkforceSchedulingDbContext db, ICurrentUser user, ShopClock shop, IEventBus bus, ILoggerFactory loggers, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        CommonRules.MaxLength(errors, "Note", r.Note, "Note", 500);
        errors.ThrowIfAny();
        var assignment = await ReviewableAsync(db, user, id, shop, requireEnded: true, ct);
        var now = shop.UtcNow;
        var running = assignment.EffectiveCheckInUtc is not null && assignment.EffectiveCheckOutUtc is null && now < shop.ToUtc(assignment.WorkDate, assignment.EndTime);
        var before = Snapshot(assignment);
        if (running)
        {
            assignment.CheckOut(now);
        }

        assignment.ApproveAsRecorded(user.UserId, string.IsNullOrWhiteSpace(r.Note) && running ? ClosedOnApprovalNote : r.Note, now);
        await db.SaveChangesAsync(ct);
        if (running)
        {
            await AuditTrail.PublishAsync(
                bus, user, loggers.CreateLogger("AICOS.Workforce.Attendance"), assignment.TenantId, "attendance.closed_on_approval", "ShiftAssignment", assignment.Id,
                before, Snapshot(assignment));
        }

        return (await ScheduleEndpoints.MapAsync(db, [assignment], ct))[0];
    }

    /// <summary>The employee forgot to check out, tapped late, or the clock was wrong: the Branch Manager sets the real times. Audited.</summary>
    private static async Task<ShiftAssignmentResponse> AdjustAsync(
        Guid id, AdjustAttendanceRequest r, WorkforceSchedulingDbContext db, ICurrentUser user, ShopClock shop, IEventBus bus, ILoggerFactory loggers, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        CommonRules.Required(errors, "Reason", r.Reason, "Reason for the correction", 500);
        if (r.CheckInAt is null && r.CheckOutAt is null)
        {
            errors.Add("CheckInAt", "Give the corrected check-in time, the corrected check-out time, or both.");
        }

        errors.ThrowIfAny();

        var assignment = await ReviewableAsync(db, user, id, shop, requireEnded: false, ct);
        var windowStart = shop.ToUtc(assignment.WorkDate, assignment.StartTime).AddHours(-6);
        var windowEnd = shop.ToUtc(assignment.WorkDate, assignment.EndTime).AddHours(6);
        foreach (var (field, value) in new[] { ("CheckInAt", r.CheckInAt), ("CheckOutAt", r.CheckOutAt) })
        {
            if (value is not null && (value.Value.UtcDateTime < windowStart || value.Value.UtcDateTime > windowEnd))
            {
                errors.Add(field, "The corrected time must be within 6 hours of the shift.");
            }

            if (value is not null && value.Value.UtcDateTime > shop.UtcNow)
            {
                errors.Add(field, "The corrected time cannot be in the future.");
            }
        }

        errors.ThrowIfAny();

        var before = Snapshot(assignment);
        assignment.Adjust(r.CheckInAt?.UtcDateTime, r.CheckOutAt?.UtcDateTime, r.Reason!, user.UserId, shop.UtcNow);
        await db.SaveChangesAsync(ct);

        await AuditTrail.PublishAsync(
            bus, user, loggers.CreateLogger("AICOS.Workforce.Attendance"), assignment.TenantId, "attendance.adjusted", "ShiftAssignment", assignment.Id,
            before, Snapshot(assignment));
        return (await ScheduleEndpoints.MapAsync(db, [assignment], ct))[0];
    }

    /// <summary>The employee did not work this shift (UC-29): recorded as an approved absence with zero hours. Audited.</summary>
    private static async Task<ShiftAssignmentResponse> AbsentAsync(
        Guid id, MarkAbsentRequest r, WorkforceSchedulingDbContext db, ICurrentUser user, ShopClock shop, IEventBus bus, ILoggerFactory loggers, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        CommonRules.Required(errors, "Reason", r.Reason, "Reason", 500);
        errors.ThrowIfAny();

        var assignment = await ReviewableAsync(db, user, id, shop, requireEnded: false, ct);
        var before = Snapshot(assignment);
        assignment.MarkAbsent(r.Reason!, user.UserId, shop.UtcNow);
        await db.SaveChangesAsync(ct);

        await AuditTrail.PublishAsync(
            bus, user, loggers.CreateLogger("AICOS.Workforce.Attendance"), assignment.TenantId, "attendance.absence_recorded", "ShiftAssignment", assignment.Id,
            before, Snapshot(assignment));
        return (await ScheduleEndpoints.MapAsync(db, [assignment], ct))[0];
    }

    /// <summary>Approves every unreviewed record of the period that is complete (check-in and check-out) and counts the rest for the manager.</summary>
    private static async Task<ApproveRangeResult> ApproveRangeAsync(
        ApproveRangeRequest r, WorkforceSchedulingDbContext db, ICurrentUser user, IBranchDirectory directory, ShopClock shop, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        if (r.From is null || r.To is null)
        {
            errors.Add("From", "Both from and to dates are required.");
        }
        else if (r.From > r.To || r.To.Value.DayNumber - r.From.Value.DayNumber >= MaxReportDays)
        {
            errors.Add("From", $"The period must be ordered and at most {MaxReportDays} days.");
        }

        errors.ThrowIfAny();

        var tenantId = TenantScope.Require(user);
        var branch = await WorkforceScope.FilterAsync(user, directory, r.BranchId, ct);
        var query = db.ShiftAssignments.Where(a => a.TenantId == tenantId && a.WorkDate >= r.From && a.WorkDate <= r.To
                                                    && a.ReviewStatus == AttendanceReviewStatus.Unreviewed
                                                    && ShiftAssignment.ActiveStatuses.Contains(a.Status));
        if (branch is not null)
        {
            query = query.Where(a => a.BranchId == branch);
        }

        if (r.EmployeeId is not null)
        {
            query = query.Where(a => a.EmployeeId == r.EmployeeId);
        }

        int approved = 0, attention = 0;
        var now = shop.UtcNow;
        foreach (var assignment in await query.ToListAsync(ct))
        {
            var ended = shop.ToUtc(assignment.WorkDate, assignment.EndTime) <= now;
            if (assignment.CheckInAtUtc is not null && assignment.CheckOutAtUtc is not null)
            {
                assignment.ApproveAsRecorded(user.UserId, null, now);
                approved++;
            }
            else if (ended)
            {
                attention++; // missing check-in or check-out: needs a correction or an absence
            }
        }

        await db.SaveChangesAsync(ct);
        return new ApproveRangeResult(approved, attention);
    }

    // ------------------------------------------------------------------ helpers
    private static async Task<ShiftAssignment> ReviewableAsync(
        WorkforceSchedulingDbContext db, ICurrentUser user, Guid id, ShopClock shop, bool requireEnded, CancellationToken ct)
    {
        var assignment = await ScheduleEndpoints.FindAsync(db, user, id, ct);
        if (!ShiftAssignment.ActiveStatuses.Contains(assignment.Status))
        {
            throw new BusinessRuleException("attendance.not_reviewable", $"A shift that is {assignment.Status} has no attendance to review.");
        }

        // Approving, correcting or marking an absence changes pay: not inside a finalized payroll period.
        await PayrollLock.EnsureOpenAsync(db, assignment.TenantId, assignment.BranchId, assignment.WorkDate, ct);

        // Approving needs something to approve: the employee has checked in, or the shift is over (and the record says what happened).
        var startsAt = shop.ToUtc(assignment.WorkDate, assignment.StartTime);
        var endsAt = shop.ToUtc(assignment.WorkDate, assignment.EndTime);
        if (requireEnded && assignment.EffectiveCheckInUtc is null && shop.UtcNow < endsAt)
        {
            throw new BusinessRuleException("attendance.shift_not_over", "The employee has not checked in and the shift has not ended yet.");
        }

        if (!requireEnded && shop.UtcNow < startsAt)
        {
            throw new BusinessRuleException("attendance.shift_not_over", "The shift has not started yet.");
        }

        return assignment;
    }

    private static object Snapshot(ShiftAssignment a) => new
    {
        a.WorkDate, a.CheckInAtUtc, a.CheckOutAtUtc, a.AdjustedCheckInAtUtc, a.AdjustedCheckOutAtUtc, a.IsAbsent,
        ReviewStatus = a.ReviewStatus.ToString(), a.ReviewNote, a.WorkedHours,
    };

    private static int GraceMinutes(IConfiguration config) => config.GetValue("Workforce:LateGraceMinutes", 5);

    private static (bool Late, int LateMinutes, bool LeftEarly) Punctuality(ShiftAssignment a, ShopClock shop, int graceMinutes)
    {
        if (a.IsAbsent)
        {
            return (false, 0, false);
        }

        var late = 0;
        if (a.EffectiveCheckInUtc is { } checkIn)
        {
            var minutes = (int)Math.Floor((checkIn - shop.ToUtc(a.WorkDate, a.StartTime)).TotalMinutes);
            late = minutes > graceMinutes ? minutes : 0;
        }

        var early = a.EffectiveCheckOutUtc is { } checkOut && shop.ToUtc(a.WorkDate, a.EndTime) - checkOut > TimeSpan.FromMinutes(graceMinutes);
        return (late > 0, late, early);
    }

    internal static AttendanceState StateOf(ShiftAssignment a, ShopClock shop, DateTime nowUtc, int graceMinutes)
    {
        _ = graceMinutes;
        if (a.Status == AssignmentStatus.Declined)
        {
            return AttendanceState.Declined;
        }

        if (a.Status == AssignmentStatus.Replaced)
        {
            return AttendanceState.Replaced;
        }

        if (a.IsAbsent)
        {
            return AttendanceState.Absent;
        }

        var startsAt = shop.ToUtc(a.WorkDate, a.StartTime);
        var endsAt = shop.ToUtc(a.WorkDate, a.EndTime);
        if (a.EffectiveCheckInUtc is null)
        {
            return nowUtc < startsAt ? AttendanceState.Upcoming : nowUtc < endsAt ? AttendanceState.InProgress : AttendanceState.NoShow;
        }

        if (a.EffectiveCheckOutUtc is null)
        {
            return nowUtc < endsAt ? AttendanceState.InProgress : AttendanceState.MissingCheckOut;
        }

        return AttendanceState.Worked;
    }

    private static async Task<ShiftAssignment> FindOwnAsync(WorkforceSchedulingDbContext db, ICurrentUser user, AttendanceRequest r, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        CommonRules.RequiredId(errors, "AssignmentId", r.AssignmentId, "Shift");
        errors.ThrowIfAny();
        return await ScheduleEndpoints.FindOwnAsync(db, user, r.AssignmentId!.Value, ct);
    }

    /// <summary>BR-50.</summary>
    /// <summary>Once the manager approved the record, corrected the times or marked an absence, the employee can no longer tap in or out of that shift.</summary>
    private static void EnsureNotReviewed(ShiftAssignment assignment)
    {
        if (assignment.ReviewStatus == AttendanceReviewStatus.Approved)
        {
            throw new ConflictException("Your manager has already reviewed this shift.", "attendance.already_reviewed");
        }
    }

    private static async Task EnsureMinIntervalAsync(WorkforceSchedulingDbContext db, Guid employeeId, DateTime nowUtc, CancellationToken ct)
    {
        var since = nowUtc.AddMinutes(-MinIntervalMinutes);
        var recent = await db.ShiftAssignments.AnyAsync(a => a.EmployeeId == employeeId
                                                             && ((a.CheckInAtUtc != null && a.CheckInAtUtc > since)
                                                                 || (a.CheckOutAtUtc != null && a.CheckOutAtUtc > since)), ct);
        if (recent)
        {
            throw new BusinessRuleException("attendance.too_soon", $"Attendance actions must be at least {MinIntervalMinutes} minutes apart.");
        }
    }
}
