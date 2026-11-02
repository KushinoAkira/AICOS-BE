using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;

namespace AICOS.Services.IntegrationTests;

/// <summary>UC-29 attendance review (BR-54, FR-82): the report, approval, corrections and absences, and who may do it.</summary>
public sealed class WorkforceReviewTests : WorkforceTestBase
{
    private const string Attendance = $"{Api}/attendance";

    /// <summary>The employee checks in at <paramref name="inAt"/> and out at <paramref name="outAt"/> (shop time on <paramref name="date"/>).</summary>
    protected async Task WorkAsync(HttpClient staff, Guid assignment, DateOnly date, (int H, int M) inAt, (int H, int M)? outAt)
    {
        SetLocal(date, inAt.H, inAt.M);
        await staff.PostOk($"{Attendance}/check-in", new { assignmentId = assignment });
        if (outAt is { } o)
        {
            SetLocal(date, o.H, o.M);
            await staff.PostOk($"{Attendance}/check-out", new { assignmentId = assignment });
        }
    }

    private async Task<JsonElement> ReportAsync(string query = "") =>
        await Manager.GetFromJsonAsync<JsonElement>($"{Attendance}/report?from={D(Monday)}&to={D(Monday.AddDays(6))}{query}");

    private static JsonElement RowOf(JsonElement report, Guid assignment) =>
        report.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("shift").GetProperty("id").GetGuid() == assignment);

    // ------------------------------------------------------------------ report (UC-29)
    [Fact]
    public async Task The_report_compares_the_schedule_with_attendance_and_summarises_each_employee()
    {
        var shift = await NewShiftAsync("Sáng", "07:00", "12:00");
        var (onTime, onTimeEmp) = await NewEmployeeAsync("An Đúng Giờ");
        var (late, lateEmp) = await NewEmployeeAsync("Bình Đi Trễ");
        var (early, earlyEmp) = await NewEmployeeAsync("Chi Về Sớm");
        var (forgot, forgotEmp) = await NewEmployeeAsync("Dung Quên Check-out");
        var (_, noShowEmp) = await NewEmployeeAsync("Em Vắng");
        var a1 = await AssignAsync(onTimeEmp, shift, Monday);
        var a2 = await AssignAsync(lateEmp, shift, Monday);
        var a3 = await AssignAsync(earlyEmp, shift, Monday);
        var a4 = await AssignAsync(forgotEmp, shift, Monday);
        var a5 = await AssignAsync(noShowEmp, shift, Monday);
        var upcoming = await AssignAsync(onTimeEmp, shift, Monday.AddDays(1));

        await WorkAsync(Staff(onTime), a1, Monday, (7, 0), (12, 0));
        await WorkAsync(Staff(late), a2, Monday, (7, 20), (12, 5));
        await WorkAsync(Staff(early), a3, Monday, (7, 0), (11, 30));
        await WorkAsync(Staff(forgot), a4, Monday, (7, 0), null);
        SetLocal(Monday, 18, 0);

        var report = await ReportAsync();

        RowOf(report, a1).GetProperty("state").GetString().Should().Be("Worked");
        RowOf(report, a1).GetProperty("workedHours").GetDouble().Should().Be(5);
        RowOf(report, a1).GetProperty("isLate").GetBoolean().Should().BeFalse();
        var lateRow = RowOf(report, a2);
        lateRow.GetProperty("isLate").GetBoolean().Should().BeTrue();
        lateRow.GetProperty("lateMinutes").GetInt32().Should().Be(20);
        RowOf(report, a3).GetProperty("leftEarly").GetBoolean().Should().BeTrue();
        RowOf(report, a3).GetProperty("workedHours").GetDouble().Should().Be(4.5);
        RowOf(report, a4).GetProperty("state").GetString().Should().Be("MissingCheckOut");
        RowOf(report, a5).GetProperty("state").GetString().Should().Be("NoShow");
        RowOf(report, upcoming).GetProperty("state").GetString().Should().Be("Upcoming");

        var summary = report.GetProperty("summary").EnumerateArray().ToDictionary(s => s.GetProperty("employeeName").GetString()!);
        summary["An Đúng Giờ"].GetProperty("shifts").GetInt32().Should().Be(2);
        summary["An Đúng Giờ"].GetProperty("workedHours").GetDouble().Should().Be(5);
        summary["An Đúng Giờ"].GetProperty("pendingReview").GetInt32().Should().Be(1);   // Monday is over and unreviewed; Tuesday is still upcoming
        summary["Bình Đi Trễ"].GetProperty("lateCount").GetInt32().Should().Be(1);
        summary["Em Vắng"].GetProperty("noShows").GetInt32().Should().Be(1);
        summary["Em Vắng"].GetProperty("workedHours").GetDouble().Should().Be(0);
    }

    [Fact]
    public async Task The_report_can_be_filtered_and_its_period_is_validated()
    {
        var shift = await NewShiftAsync("Sáng", "07:00", "12:00");
        var (_, one) = await NewEmployeeAsync("An");
        var (_, two) = await NewEmployeeAsync("Bình");
        await AssignAsync(one, shift, Monday);
        await AssignAsync(two, shift, Monday);

        (await ReportAsync($"&employeeId={one}")).GetProperty("rows").GetArrayLength().Should().Be(1);
        (await ReportAsync("&reviewStatus=Approved")).GetProperty("rows").GetArrayLength().Should().Be(0);
        (await ReportAsync("&reviewStatus=Unreviewed")).GetProperty("rows").GetArrayLength().Should().Be(2);

        await Manager.GetAsync($"{Attendance}/report?from={D(Monday.AddDays(3))}&to={D(Monday)}").ShouldBe(400);
        await Manager.GetAsync($"{Attendance}/report?from={D(Monday)}&to={D(Monday.AddDays(62))}").ShouldBe(400);
        (await Manager.GetFromJsonAsync<JsonElement>($"{Attendance}/report")).GetProperty("rows").GetArrayLength().Should().Be(2); // defaults to the last 7 days up to today
    }

    // ------------------------------------------------------------------ approve / adjust / absent (BR-54, FR-82)
    [Fact]
    public async Task A_running_shift_can_be_approved_and_is_closed_at_that_moment()
    {
        var shift = await NewShiftAsync("Sáng", "07:00", "12:00");
        var (userId, employee) = await NewEmployeeAsync("An");
        var id = await AssignAsync(employee, shift, Monday);
        await WorkAsync(Staff(userId), id, Monday, (7, 0), null);
        SetLocal(Monday, 10, 0);

        var approved = await Manager.PostOk($"{Attendance}/{id}/approve", new { });

        approved.GetProperty("reviewStatus").GetString().Should().Be("Approved");
        approved.GetProperty("workedHours").GetDouble().Should().Be(3);
        approved.GetProperty("reviewNote").GetString().Should().Contain("Duyệt trong ca");
        approved.GetProperty("checkOutAtUtc").ValueKind.Should().NotBe(JsonValueKind.Null);
        F.Events.Published.OfType<AuditRecordedEvent>().Should().ContainSingle(e => e.Action == "attendance.closed_on_approval");

        SetLocal(Monday, 10, 10);
        await Staff(userId).Post($"{Attendance}/check-out", new { assignmentId = id }).ShouldBe(409, "attendance.already_checked_out");
        await Manager.Post($"{Attendance}/{id}/approve", new { note = "Lần hai" }).ShouldBe(200);
    }

    [Fact]
    public async Task A_record_checked_out_early_is_approved_before_the_shift_ends_without_touching_the_times()
    {
        var shift = await NewShiftAsync("Sáng", "07:00", "12:00");
        var (userId, employee) = await NewEmployeeAsync("An");
        var id = await AssignAsync(employee, shift, Monday);
        await WorkAsync(Staff(userId), id, Monday, (7, 0), (9, 0));
        SetLocal(Monday, 9, 30);

        var approved = await Manager.PostOk($"{Attendance}/{id}/approve", new { });

        approved.GetProperty("workedHours").GetDouble().Should().Be(2);
        approved.GetProperty("reviewNote").ValueKind.Should().Be(JsonValueKind.Null);
        F.Events.Published.OfType<AuditRecordedEvent>().Should().NotContain(e => e.Action == "attendance.closed_on_approval");
    }

    [Fact]
    public async Task Nothing_to_approve_while_the_employee_has_not_checked_in()
    {
        var shift = await NewShiftAsync("Sáng", "07:00", "12:00");
        var (_, employee) = await NewEmployeeAsync("An");
        var id = await AssignAsync(employee, shift, Monday);

        SetLocal(Monday, 6, 0);
        await Manager.Post($"{Attendance}/{id}/approve", new { }).ShouldBe(422, "attendance.shift_not_over");
        SetLocal(Monday, 10, 0);
        await Manager.Post($"{Attendance}/{id}/approve", new { }).ShouldBe(422, "attendance.shift_not_over");
        SetLocal(Monday, 12, 30);
        await Manager.Post($"{Attendance}/{id}/approve", new { }).ShouldBe(422, "attendance.incomplete");
    }

    [Fact]
    public async Task A_complete_record_is_approved_as_recorded_and_a_forgotten_check_out_needs_a_correction()
    {
        var shift = await NewShiftAsync("Sáng", "07:00", "12:00");
        var (userId, employee) = await NewEmployeeAsync("An");
        var id = await AssignAsync(employee, shift, Monday);
        await WorkAsync(Staff(userId), id, Monday, (7, 0), null);

        SetLocal(Monday, 12, 30);
        await Manager.Post($"{Attendance}/{id}/approve", new { }).ShouldBe(422, "attendance.incomplete");        // no check-out yet

        SetLocal(Monday, 12, 5);
        await Staff(userId).PostOk($"{Attendance}/check-out", new { assignmentId = id });
        SetLocal(Monday, 12, 30);
        var approved = await Manager.PostOk($"{Attendance}/{id}/approve", new { note = "Đủ công" });
        approved.GetProperty("reviewStatus").GetString().Should().Be("Approved");
        approved.GetProperty("reviewNote").GetString().Should().Be("Đủ công");
        approved.GetProperty("workedHours").GetDouble().Should().Be(5.08);
    }

    [Fact]
    public async Task The_manager_corrects_a_forgotten_check_out_and_the_correction_is_approved_and_audited()
    {
        var shift = await NewShiftAsync("Sáng", "07:00", "12:00");
        var (userId, employee) = await NewEmployeeAsync("An");
        var id = await AssignAsync(employee, shift, Monday);
        await WorkAsync(Staff(userId), id, Monday, (7, 0), null);
        SetLocal(Monday, 13, 0);
        var checkOut = new DateTimeOffset(Monday.ToDateTime(new TimeOnly(12, 0)), TimeSpan.FromHours(7));

        var corrected = await Manager.PostOk($"{Attendance}/{id}/adjust", new { checkOutAt = checkOut, reason = "Quên check-out, xác nhận với nhân viên" });

        corrected.GetProperty("reviewStatus").GetString().Should().Be("Approved");
        corrected.GetProperty("workedHours").GetDouble().Should().Be(5);
        corrected.GetProperty("adjustedCheckOutAtUtc").GetDateTime().Should().Be(checkOut.UtcDateTime);
        corrected.GetProperty("checkOutAtUtc").ValueKind.Should().Be(JsonValueKind.Null);   // what the employee recorded stays untouched

        var audit = F.Events.Published.OfType<AuditRecordedEvent>().Where(e => !e.Action.StartsWith("attendance.checked_")).Single();
        audit.Action.Should().Be("attendance.adjusted");
        audit.EntityType.Should().Be("ShiftAssignment");
        audit.TenantId.Should().Be(Tenant);
        audit.ActorRole.Should().Be(AicosRoles.BranchManager);
        audit.Before.Should().Contain("\"reviewStatus\":\"Unreviewed\"");
        audit.After.Should().Contain("\"reviewStatus\":\"Approved\"").And.Contain("\"workedHours\":5");
    }

    [Fact]
    public async Task Corrections_are_validated()
    {
        var shift = await NewShiftAsync("Sáng", "07:00", "12:00");
        var (userId, employee) = await NewEmployeeAsync("An");
        var id = await AssignAsync(employee, shift, Monday);
        await WorkAsync(Staff(userId), id, Monday, (7, 0), null);
        SetLocal(Monday, 13, 0);
        DateTimeOffset At(int day, int h) => new(Monday.AddDays(day).ToDateTime(new TimeOnly(h, 0)), TimeSpan.FromHours(7));

        await Manager.Post($"{Attendance}/{id}/adjust", new { checkOutAt = At(0, 12) }).ShouldBe(400);                              // a reason is required
        await Manager.Post($"{Attendance}/{id}/adjust", new { reason = "x" }).ShouldBe(400);                                       // at least one time
        await Manager.Post($"{Attendance}/{id}/adjust", new { checkOutAt = At(1, 12), reason = "x" }).ShouldBe(400);              // far from the shift
        await Manager.Post($"{Attendance}/{id}/adjust", new { checkOutAt = At(0, 14), reason = "x" }).ShouldBe(400);              // in the future (clock is 13:00)
        await Manager.Post($"{Attendance}/{id}/adjust", new { checkOutAt = At(0, 6), reason = "x" }).ShouldBe(422, "attendance.invalid_times"); // before the check-in
        await Manager.Post($"{Attendance}/{id}/adjust", new { checkInAt = At(0, 12), reason = "x" }).ShouldBe(422, "attendance.invalid_times");

        var ok = await Manager.Post($"{Attendance}/{id}/adjust", new { checkOutAt = At(0, 12), reason = "ok" });
        ok.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        await Manager.Post($"{Attendance}/{id}/adjust", new { checkOutAt = At(0, 11), reason = "chỉnh lại" }).ShouldBe(200);        // an approved record can be corrected again
        (await GetAssignmentAsync(id)).GetProperty("workedHours").GetDouble().Should().Be(4);
    }

    [Fact]
    public async Task An_absence_is_recorded_with_zero_hours_and_audited_and_cannot_precede_the_shift()
    {
        var shift = await NewShiftAsync("Sáng", "07:00", "12:00");
        var (_, employee) = await NewEmployeeAsync("An");
        var id = await AssignAsync(employee, shift, Monday);

        SetLocal(Monday, 6, 0);
        await Manager.Post($"{Attendance}/{id}/absent", new { reason = "Nghỉ không phép" }).ShouldBe(422, "attendance.shift_not_over");
        SetLocal(Monday, 12, 30);
        await Manager.Post($"{Attendance}/{id}/absent", new { reason = "" }).ShouldBe(400);

        var absent = await Manager.PostOk($"{Attendance}/{id}/absent", new { reason = "Nghỉ không phép" });
        absent.GetProperty("isAbsent").GetBoolean().Should().BeTrue();
        absent.GetProperty("reviewStatus").GetString().Should().Be("Approved");
        absent.GetProperty("workedHours").GetDouble().Should().Be(0);
        (await ReportAsync()).GetProperty("summary")[0].GetProperty("absences").GetInt32().Should().Be(1);

        F.Events.Published.OfType<AuditRecordedEvent>().Where(e => !e.Action.StartsWith("attendance.checked_")).Single().Action.Should().Be("attendance.absence_recorded");
    }

    [Fact]
    public async Task Declined_and_replaced_shifts_have_no_attendance_to_review()
    {
        var shift = await NewShiftAsync("Sáng", "07:00", "12:00");
        var (userId, employee) = await NewEmployeeAsync("An");
        var id = await AssignAsync(employee, shift, Monday);
        await Staff(userId).PostOk($"{Api}/shift-assignments/{id}/decline", new { reason = "Ốm" });
        SetLocal(Monday, 13, 0);

        await Manager.Post($"{Attendance}/{id}/approve", new { }).ShouldBe(422, "attendance.not_reviewable");
        await Manager.Post($"{Attendance}/{id}/absent", new { reason = "x" }).ShouldBe(422, "attendance.not_reviewable");
        (await ReportAsync()).GetProperty("rows")[0].GetProperty("state").GetString().Should().Be("Declined");
    }

    [Fact]
    public async Task Approving_a_whole_period_takes_the_complete_records_and_flags_the_rest()
    {
        var shift = await NewShiftAsync("Sáng", "07:00", "12:00");
        var (okUser, okEmp) = await NewEmployeeAsync("An");
        var (forgotUser, forgotEmp) = await NewEmployeeAsync("Bình");
        var (_, ghostEmp) = await NewEmployeeAsync("Chi");
        var good = await AssignAsync(okEmp, shift, Monday);
        var bad = await AssignAsync(forgotEmp, shift, Monday);
        await AssignAsync(ghostEmp, shift, Monday);
        var tomorrow = await AssignAsync(okEmp, shift, Monday.AddDays(1));
        await WorkAsync(Staff(okUser), good, Monday, (7, 0), (12, 0));
        await WorkAsync(Staff(forgotUser), bad, Monday, (7, 0), null);
        SetLocal(Monday, 18, 0);

        var result = await Manager.PostOk($"{Attendance}/approve-range", new { from = D(Monday), to = D(Monday.AddDays(6)) });

        result.GetProperty("approved").GetInt32().Should().Be(1);
        result.GetProperty("needsAttention").GetInt32().Should().Be(2);       // a forgotten check-out and a no-show
        (await GetAssignmentAsync(good)).GetProperty("reviewStatus").GetString().Should().Be("Approved");
        (await GetAssignmentAsync(bad)).GetProperty("reviewStatus").GetString().Should().Be("Unreviewed");
        (await GetAssignmentAsync(tomorrow)).GetProperty("reviewStatus").GetString().Should().Be("Unreviewed");   // not over yet
        (await Manager.PostOk($"{Attendance}/approve-range", new { from = D(Monday), to = D(Monday.AddDays(6)) })).GetProperty("approved").GetInt32().Should().Be(0);

        await Manager.Post($"{Attendance}/approve-range", new { }).ShouldBe(400);
        await Manager.Post($"{Attendance}/approve-range", new { from = D(Monday.AddDays(3)), to = D(Monday) }).ShouldBe(400);
        await Manager.Post($"{Attendance}/approve-range", new { from = D(Monday), to = D(Monday.AddDays(70)) }).ShouldBe(400);
    }

    // ------------------------------------------------------------------ who may review
    [Fact]
    public async Task Only_managers_of_the_branch_review_attendance_and_other_brands_see_nothing()
    {
        var shift = await NewShiftAsync("Sáng", "07:00", "12:00");
        var (userId, employee) = await NewEmployeeAsync("An");
        var id = await AssignAsync(employee, shift, Monday);
        await WorkAsync(Staff(userId), id, Monday, (7, 0), (12, 0));
        SetLocal(Monday, 13, 0);

        await Staff(userId).Post($"{Attendance}/{id}/approve", new { }).ShouldBe(403);
        await F.As(AicosRoles.Cashier, B1).GetAsync($"{Attendance}/report").ShouldBe(403);
        await F.As(AicosRoles.Customer).GetAsync($"{Attendance}/report").ShouldBe(403);
        await Manager2.Post($"{Attendance}/{id}/approve", new { }).ShouldBe(403, "branch.forbidden");
        await OtherAdmin.Post($"{Attendance}/{id}/approve", new { }).ShouldBe(404);
        await OtherAdmin.Post($"{Attendance}/{id}/adjust", new { checkOutAt = DateTimeOffset.UtcNow, reason = "x" }).ShouldBe(404);
        await OtherAdmin.Post($"{Attendance}/{id}/absent", new { reason = "x" }).ShouldBe(404);

        (await Manager2.GetFromJsonAsync<JsonElement>($"{Attendance}/report?from={D(Monday)}&to={D(Monday)}")).GetProperty("rows").GetArrayLength().Should().Be(0);
        (await OtherAdmin.GetFromJsonAsync<JsonElement>($"{Attendance}/report?from={D(Monday)}&to={D(Monday)}")).GetProperty("rows").GetArrayLength().Should().Be(0);
        (await Admin.GetFromJsonAsync<JsonElement>($"{Attendance}/report?from={D(Monday)}&to={D(Monday)}")).GetProperty("rows").GetArrayLength().Should().Be(1);
        await Manager.PostOk($"{Attendance}/{id}/approve", new { });
    }
}
