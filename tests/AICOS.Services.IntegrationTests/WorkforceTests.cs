using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;

namespace AICOS.Services.IntegrationTests;

/// <summary>UC-27 staff profiles, shift templates, UC-28 scheduling and UC-48 check-in/out, with tenant and branch isolation.</summary>
public sealed class WorkforceTests : WorkforceTestBase
{
    // ------------------------------------------------------------------ employees (UC-27)
    [Fact]
    public async Task A_branch_manager_creates_a_profile_for_a_barista_of_their_branch_and_the_name_comes_from_the_account()
    {
        var userId = NewAccount(AicosRoles.Barista, B1, "Nguyễn Văn Pha");

        var employee = await Manager.PostOk($"{Api}/employees", new { userId, contractType = "FullTime", position = "Barista chính", hireDate = "2026-01-15" });

        employee.GetProperty("fullName").GetString().Should().Be("Nguyễn Văn Pha");
        employee.GetProperty("role").GetString().Should().Be("Barista");
        employee.GetProperty("contractType").GetString().Should().Be("FullTime");
        employee.GetProperty("branchId").GetGuid().Should().Be(B1);
        employee.GetProperty("status").GetString().Should().Be("Active");
        (await Staff(userId).GetFromJsonAsync<JsonElement>($"{Api}/employees/me")).Id().Should().Be(employee.Id());
    }

    [Fact]
    public async Task A_paused_template_is_deleted_when_no_coming_shift_uses_it_and_one_with_history_is_hidden_but_kept()
    {
        var unused = await NewShiftAsync("Thử", "08:00", "10:00");
        await Manager.DeleteAsync($"{Api}/work-shifts/{unused}").ShouldBe(422, "work_shift.active");       // pause it first
        await Manager.Patch($"{Api}/work-shifts/{unused}/status", new { isActive = false }).ShouldBe(200);
        await Manager.DeleteAsync($"{Api}/work-shifts/{unused}").ShouldBe(204);
        await Manager.GetAsync($"{Api}/work-shifts/{unused}").ShouldBe(404);

        var used = await NewShiftAsync("Đêm", "13:00", "17:00");
        var (_, employee) = await NewEmployeeAsync("Lan Cũ");
        var assignment = await AssignAsync(employee, used, Monday.AddDays(1));
        await Manager.Patch($"{Api}/work-shifts/{used}/status", new { isActive = false }).ShouldBe(200);
        await Manager.DeleteAsync($"{Api}/work-shifts/{used}").ShouldBe(409, "work_shift.in_use");       // a coming shift uses it

        SetLocal(Monday.AddDays(3), 9);                                                                    // that shift is now in the past
        await Manager.DeleteAsync($"{Api}/work-shifts/{used}").ShouldBe(204);
        var listed = await Manager.GetFromJsonAsync<JsonElement>($"{Api}/work-shifts");
        listed.EnumerateArray().Select(s => s.GetProperty("id").GetGuid()).Should().NotContain(used);
        (await Manager.GetFromJsonAsync<JsonElement>($"{Api}/shift-assignments/{assignment}")).GetProperty("shiftName").GetString().Should().Be("Đêm"); // history keeps its name
        (await Manager.PostOk($"{Api}/work-shifts", new { name = "Đêm", startTime = "13:00", endTime = "17:00" })).Id().Should().NotBe(used); // the name is free again
    }

    [Fact]
    public async Task Employee_input_and_the_linked_account_are_validated()
    {
        var barista = NewAccount(AicosRoles.Barista, B1, "An");
        var manager = NewAccount(AicosRoles.BranchManager, B1, "Quản lý");
        var otherBranch = NewAccount(AicosRoles.Cashier, B2, "Bình");
        var locked = NewAccount(AicosRoles.Cashier, B1, "Chi", active: false);
        var foreign = NewAccount(AicosRoles.Cashier, OtherBranch, "Lan", OtherTenant);

        await Manager.Post($"{Api}/employees", new { userId = barista }).ShouldBe(400);                                            // contract type required
        await Manager.Post($"{Api}/employees", new { contractType = "PartTime" }).ShouldBe(400);                                  // user required
        await Manager.Post($"{Api}/employees", new { userId = barista, contractType = "Contractor" }).ShouldBe(400);
        await Manager.Post($"{Api}/employees", new { userId = barista, contractType = "PartTime", position = new string('p', 51) }).ShouldBe(400);
        await Manager.Post($"{Api}/employees", new { userId = Guid.NewGuid(), contractType = "PartTime" }).ShouldBe(400);        // no such account
        await Manager.Post($"{Api}/employees", new { userId = manager, contractType = "PartTime" }).ShouldBe(400);               // only barista and cashier
        await Manager.Post($"{Api}/employees", new { userId = otherBranch, contractType = "PartTime" }).ShouldBe(400);           // another branch
        await Manager.Post($"{Api}/employees", new { userId = locked, contractType = "PartTime" }).ShouldBe(400);
        await Manager.Post($"{Api}/employees", new { userId = foreign, contractType = "PartTime" }).ShouldBe(400);               // another brand
    }

    [Fact]
    public async Task One_account_has_one_profile_and_only_managers_manage_profiles()
    {
        var userId = NewAccount(AicosRoles.Cashier, B1, "Thu");
        await Manager.PostOk($"{Api}/employees", new { userId, contractType = "PartTime" });

        await Manager.Post($"{Api}/employees", new { userId, contractType = "FullTime" }).ShouldBe(409, "employee.duplicate_user");
        var other = NewAccount(AicosRoles.Cashier, B1, "Mai");
        await Staff(userId, AicosRoles.Cashier).Post($"{Api}/employees", new { userId = other, contractType = "PartTime" }).ShouldBe(403);
        await F.As(AicosRoles.Barista, B1).GetAsync($"{Api}/employees").ShouldBe(403);
        await F.As(AicosRoles.Customer).GetAsync($"{Api}/employees").ShouldBe(403);
        await F.As(AicosRoles.SuperAdmin).GetAsync($"{Api}/employees").ShouldBe(403);
        await F.CreateClient().GetAsync($"{Api}/employees").ShouldBe(401);
    }

    [Fact]
    public async Task The_brand_admin_creates_profiles_for_any_branch_of_the_brand_but_must_name_it()
    {
        var userId = NewAccount(AicosRoles.Barista, B2, "Dung");

        await Admin.Post($"{Api}/employees", new { userId, contractType = "PartTime" }).ShouldBe(400);                              // which branch?
        await Admin.Post($"{Api}/employees", new { branchId = OtherBranch, userId, contractType = "PartTime" }).ShouldBe(400);      // a foreign branch
        var employee = await Admin.PostOk($"{Api}/employees", new { branchId = B2, userId, contractType = "PartTime" });
        employee.GetProperty("branchId").GetGuid().Should().Be(B2);
        await Manager.Post($"{Api}/employees", new { branchId = B2, userId = NewAccount(AicosRoles.Barista, B2, "X"), contractType = "PartTime" }).ShouldBe(403, "branch.forbidden");
    }

    [Fact]
    public async Task A_suspended_branch_gets_no_new_profiles_or_shift_templates_BR08()
    {
        var userId = NewAccount(AicosRoles.Barista, B2, "Dung");
        F.Branches.Suspend(B2);

        await Admin.Post($"{Api}/employees", new { branchId = B2, userId, contractType = "PartTime" }).ShouldBe(422, "branch.suspended");
        await Admin.Post($"{Api}/work-shifts", new { branchId = B2, name = "Sáng", startTime = "07:00", endTime = "12:00" }).ShouldBe(422, "branch.suspended");
    }

    [Fact]
    public async Task Profiles_can_be_edited_listed_and_filtered()
    {
        var (_, one) = await NewEmployeeAsync("An Pha Chế", contract: "PartTime");
        await NewEmployeeAsync("Bình Thu Ngân", AicosRoles.Cashier, "FullTime");
        await NewEmployeeAsync("Chi Chi Nhánh Hai", contract: "FullTime", branch: B2);

        var edited = await Manager.Put($"{Api}/employees/{one}", new { fullName = "An Pha Chế Mới", position = "Trưởng ca", contractType = "FullTime" });
        (await edited.Body()).GetProperty("contractType").GetString().Should().Be("FullTime");
        await Manager.Put($"{Api}/employees/{one}", new { fullName = "", contractType = "FullTime" }).ShouldBe(400);
        await Manager.Put($"{Api}/employees/{one}", new { fullName = "X" }).ShouldBe(400);

        (await Manager.GetFromJsonAsync<JsonElement>($"{Api}/employees")).GetProperty("totalCount").GetInt32().Should().Be(2);        // own branch only
        (await Manager.GetFromJsonAsync<JsonElement>($"{Api}/employees?contractType=PartTime")).GetProperty("totalCount").GetInt32().Should().Be(0);
        (await Manager.GetFromJsonAsync<JsonElement>($"{Api}/employees?search=thu")).GetProperty("totalCount").GetInt32().Should().Be(1);
        (await Admin.GetFromJsonAsync<JsonElement>($"{Api}/employees")).GetProperty("totalCount").GetInt32().Should().Be(3);          // the whole brand
        (await Admin.GetFromJsonAsync<JsonElement>($"{Api}/employees?branchId={B2}")).GetProperty("totalCount").GetInt32().Should().Be(1);
        await Manager.GetAsync($"{Api}/employees?branchId={B2}").ShouldBe(403, "branch.forbidden");
    }

    [Fact]
    public async Task Another_brand_and_another_branch_cannot_see_or_change_a_profile()
    {
        var (_, id) = await NewEmployeeAsync("An");

        await OtherAdmin.GetAsync($"{Api}/employees/{id}").ShouldBe(404);
        await OtherAdmin.Put($"{Api}/employees/{id}", new { fullName = "X", contractType = "PartTime" }).ShouldBe(404);
        await OtherAdmin.Patch($"{Api}/employees/{id}/status", new { status = "Inactive" }).ShouldBe(404);
        (await OtherAdmin.GetFromJsonAsync<JsonElement>($"{Api}/employees")).GetProperty("totalCount").GetInt32().Should().Be(0);

        await Manager2.GetAsync($"{Api}/employees/{id}").ShouldBe(403, "branch.forbidden");
        await Manager2.Put($"{Api}/employees/{id}", new { fullName = "X", contractType = "PartTime" }).ShouldBe(403, "branch.forbidden");
    }

    [Fact]
    public async Task An_employee_with_upcoming_shifts_cannot_be_deactivated()
    {
        var (_, id) = await NewEmployeeAsync("An");
        var shift = await NewShiftAsync("Sáng", "07:00", "12:00");
        var assignment = await AssignAsync(id, shift, Monday.AddDays(1));

        await Manager.Patch($"{Api}/employees/{id}/status", new { status = "Inactive" }).ShouldBe(409, "employee.has_upcoming_shifts");
        await Manager.Patch($"{Api}/employees/{id}/status", new { }).ShouldBe(400);

        await Manager.DeleteAsync($"{Api}/shift-assignments/{assignment}").ShouldBe(204);
        await Manager.Patch($"{Api}/employees/{id}/status", new { status = "Inactive" }).ShouldBe(200);
        await Manager.Post($"{Api}/shift-assignments", new { employeeId = id, workShiftId = shift, workDate = D(Monday.AddDays(2)) }).ShouldBe(400); // inactive
    }

    // ------------------------------------------------------------------ shift templates
    [Fact]
    public async Task Shift_templates_are_validated_unique_per_branch_and_editable()
    {
        var shift = await Manager.PostOk($"{Api}/work-shifts", new { name = "Ca sáng", startTime = "06:00", endTime = "12:00" });
        shift.GetProperty("hours").GetDouble().Should().Be(6);

        await Manager.Post($"{Api}/work-shifts", new { name = "ca sáng", startTime = "13:00", endTime = "17:00" }).ShouldBe(409, "shift.duplicate_name");
        await Manager.Post($"{Api}/work-shifts", new { name = "", startTime = "06:00", endTime = "12:00" }).ShouldBe(400);
        await Manager.Post($"{Api}/work-shifts", new { name = "Đêm", startTime = "22:00", endTime = "02:00" }).ShouldBe(400);        // no overnight shifts
        await Manager.Post($"{Api}/work-shifts", new { name = "Ngắn", startTime = "10:00", endTime = "10:30" }).ShouldBe(400);
        await Manager.Post($"{Api}/work-shifts", new { name = "Dài", startTime = "05:00", endTime = "19:00" }).ShouldBe(400);
        await Manager2.PostOk($"{Api}/work-shifts", new { name = "Ca sáng", startTime = "06:00", endTime = "12:00" });                 // another branch, same name

        var edited = await Manager.Put($"{Api}/work-shifts/{shift.Id()}", new { name = "Ca sáng sớm", startTime = "05:30", endTime = "11:30" });
        (await edited.Body()).GetProperty("name").GetString().Should().Be("Ca sáng sớm");
        (await Manager.Patch($"{Api}/work-shifts/{shift.Id()}/status", new { isActive = false })).StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        await Manager.Patch($"{Api}/work-shifts/{shift.Id()}/status", new { }).ShouldBe(400);
        (await Manager.GetFromJsonAsync<JsonElement>($"{Api}/work-shifts?activeOnly=true")).GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Staff_read_the_shift_templates_of_their_branch_but_cannot_change_them()
    {
        var shift = await NewShiftAsync("Ca sáng", "06:00", "12:00");
        var cashier = F.As(AicosRoles.Cashier, B1);

        (await cashier.GetFromJsonAsync<JsonElement>($"{Api}/work-shifts")).GetArrayLength().Should().Be(1);
        await cashier.Post($"{Api}/work-shifts", new { name = "X", startTime = "06:00", endTime = "12:00" }).ShouldBe(403);
        await cashier.Put($"{Api}/work-shifts/{shift}", new { name = "X", startTime = "06:00", endTime = "12:00" }).ShouldBe(403);
        await F.As(AicosRoles.Cashier, B2).GetAsync($"{Api}/work-shifts?branchId={B1}").ShouldBe(403, "branch.forbidden");
        await OtherAdmin.GetAsync($"{Api}/work-shifts/{shift}").ShouldBe(404);
        await Manager2.GetAsync($"{Api}/work-shifts/{shift}").ShouldBe(403, "branch.forbidden");
    }

    // ------------------------------------------------------------------ scheduling (UC-28, BR-48)
    [Fact]
    public async Task A_manager_assigns_a_shift_and_the_employee_confirms_it()
    {
        var (userId, employee) = await NewEmployeeAsync("An");
        var shift = await NewShiftAsync("Sáng", "07:00", "12:00");

        var id = await AssignAsync(employee, shift, Monday.AddDays(1));
        var assignment = await GetAssignmentAsync(id);
        assignment.GetProperty("status").GetString().Should().Be("Assigned");
        assignment.GetProperty("startTime").GetString().Should().Be("07:00");
        assignment.GetProperty("shiftName").GetString().Should().Be("Sáng");
        assignment.GetProperty("isReplacement").GetBoolean().Should().BeFalse();

        var staff = Staff(userId);
        (await staff.GetFromJsonAsync<JsonElement>($"{Api}/shift-assignments/mine?from={D(Monday)}&to={D(Monday.AddDays(7))}")).GetArrayLength().Should().Be(1);
        await staff.PostOk($"{Api}/shift-assignments/{id}/confirm", new { });
        (await GetAssignmentAsync(id)).GetProperty("status").GetString().Should().Be("Confirmed");
        await staff.Post($"{Api}/shift-assignments/{id}/confirm", new { }).ShouldBe(422, "shift.invalid_status");
    }

    [Fact]
    public async Task Overlapping_shifts_and_too_many_weekly_hours_are_refused_BR48()
    {
        var (_, employee) = await NewEmployeeAsync("An");
        var morning = await NewShiftAsync("Sáng", "07:00", "12:00");
        var overlap = await NewShiftAsync("Giữa ngày", "11:00", "15:00");
        var afternoon = await NewShiftAsync("Chiều", "13:00", "21:00");
        await AssignAsync(employee, morning, Monday.AddDays(1));

        await Manager.Post($"{Api}/shift-assignments", new { employeeId = employee, workShiftId = overlap, workDate = D(Monday.AddDays(1)) }).ShouldBe(409, "shift.overlap");
        await AssignAsync(employee, afternoon, Monday.AddDays(1));                                       // back to back is fine: 5h + 8h = 13h

        // Three full days give 39 hours; a morning shift on day four makes 44; the afternoon shift would make 52 (max 48).
        foreach (var day in new[] { 2, 3 })
        {
            await AssignAsync(employee, afternoon, Monday.AddDays(day));
            await AssignAsync(employee, morning, Monday.AddDays(day));
        }

        await AssignAsync(employee, morning, Monday.AddDays(4));
        await Manager.Post($"{Api}/shift-assignments", new { employeeId = employee, workShiftId = afternoon, workDate = D(Monday.AddDays(4)) })
            .ShouldBe(422, "shift.weekly_hours_exceeded");
    }

    [Fact]
    public async Task Scheduling_input_is_validated()
    {
        var (_, employee) = await NewEmployeeAsync("An");
        var (_, other) = await NewEmployeeAsync("Bình", branch: B2);
        var shift = await NewShiftAsync("Sáng", "07:00", "12:00");
        var shift2 = await NewShiftAsync("Sáng 2", "07:00", "12:00", B2);

        await Manager.Post($"{Api}/shift-assignments", new { workShiftId = shift, workDate = D(Monday) }).ShouldBe(400);
        await Manager.Post($"{Api}/shift-assignments", new { employeeId = employee, workDate = D(Monday) }).ShouldBe(400);
        await Manager.Post($"{Api}/shift-assignments", new { employeeId = employee, workShiftId = shift }).ShouldBe(400);
        await Manager.Post($"{Api}/shift-assignments", new { employeeId = employee, workShiftId = shift, workDate = D(Monday.AddDays(-1)) }).ShouldBe(400);    // past
        await Manager.Post($"{Api}/shift-assignments", new { employeeId = employee, workShiftId = shift, workDate = D(Monday.AddDays(61)) }).ShouldBe(400);    // too far
        await Manager.Post($"{Api}/shift-assignments", new { employeeId = employee, workShiftId = shift2, workDate = D(Monday) }).ShouldBe(403, "branch.forbidden"); // another branch template
        await Admin.Post($"{Api}/shift-assignments", new { employeeId = other, workShiftId = shift, workDate = D(Monday) }).ShouldBe(400);               // template of another branch
        await Manager.Post($"{Api}/shift-assignments", new { employeeId = Guid.NewGuid(), workShiftId = shift, workDate = D(Monday) }).ShouldBe(404);
        await Manager.Patch($"{Api}/work-shifts/{shift}/status", new { isActive = false }).ShouldBe(200);
        await Manager.Post($"{Api}/shift-assignments", new { employeeId = employee, workShiftId = shift, workDate = D(Monday) }).ShouldBe(400);              // inactive template
    }

    [Fact]
    public async Task An_employee_can_decline_and_the_manager_sees_it_but_a_started_shift_cannot_be_removed()
    {
        var (userId, employee) = await NewEmployeeAsync("An");
        var shift = await NewShiftAsync("Sáng", "07:00", "12:00");
        var id = await AssignAsync(employee, shift, Monday);
        var staff = Staff(userId);

        await staff.Post($"{Api}/shift-assignments/{id}/decline", new { reason = "" }).ShouldBe(400);
        await staff.PostOk($"{Api}/shift-assignments/{id}/decline", new { reason = "Ốm" });
        var declined = await GetAssignmentAsync(id);
        declined.GetProperty("status").GetString().Should().Be("Declined");
        declined.GetProperty("declineReason").GetString().Should().Be("Ốm");
        (await Manager.GetFromJsonAsync<JsonElement>($"{Api}/shift-assignments?status=Declined")).GetProperty("totalCount").GetInt32().Should().Be(1);

        var second = await AssignAsync(employee, shift, Monday.AddDays(1));
        SetLocal(Monday.AddDays(1), 6, 50);
        await staff.PostOk($"{Api}/attendance/check-in", new { assignmentId = second });
        await Manager.DeleteAsync($"{Api}/shift-assignments/{second}").ShouldBe(409, "shift.already_started");
    }

    [Fact]
    public async Task Staff_only_see_their_own_shifts_and_other_brands_see_nothing()
    {
        var (userA, employeeA) = await NewEmployeeAsync("An");
        var (userB, _) = await NewEmployeeAsync("Bình");
        var shift = await NewShiftAsync("Sáng", "07:00", "12:00");
        var id = await AssignAsync(employeeA, shift, Monday.AddDays(1));

        await Staff(userA).GetAsync($"{Api}/shift-assignments/{id}").ShouldBe(200);
        await Staff(userB).GetAsync($"{Api}/shift-assignments/{id}").ShouldBe(404);                         // a colleague's shift
        await Staff(userB).Post($"{Api}/shift-assignments/{id}/confirm", new { }).ShouldBe(404);
        await Staff(userB).Post($"{Api}/shift-assignments/{id}/decline", new { reason = "x" }).ShouldBe(404);
        await OtherAdmin.GetAsync($"{Api}/shift-assignments/{id}").ShouldBe(404);
        await OtherAdmin.DeleteAsync($"{Api}/shift-assignments/{id}").ShouldBe(404);
        await Manager2.GetAsync($"{Api}/shift-assignments/{id}").ShouldBe(403, "branch.forbidden");
        await Manager2.DeleteAsync($"{Api}/shift-assignments/{id}").ShouldBe(403, "branch.forbidden");
        (await Manager2.GetFromJsonAsync<JsonElement>($"{Api}/shift-assignments")).GetProperty("totalCount").GetInt32().Should().Be(0);
        (await Admin.GetFromJsonAsync<JsonElement>($"{Api}/shift-assignments")).GetProperty("totalCount").GetInt32().Should().Be(1);
    }

    // ------------------------------------------------------------------ check-in / check-out (UC-48, BR-49, BR-50)
    [Fact]
    public async Task Check_in_opens_15_minutes_before_the_shift_and_closes_when_it_ends_BR49()
    {
        var (userId, employee) = await NewEmployeeAsync("An");
        var shift = await NewShiftAsync("Sáng", "07:00", "12:00");
        var id = await AssignAsync(employee, shift, Monday);
        var staff = Staff(userId);

        SetLocal(Monday, 6, 40);
        await staff.Post($"{Api}/attendance/check-in", new { assignmentId = id }).ShouldBe(422, "attendance.outside_window");   // too early
        SetLocal(Monday, 12, 0);
        await staff.Post($"{Api}/attendance/check-in", new { assignmentId = id }).ShouldBe(422, "attendance.outside_window");   // shift is over
        SetLocal(Monday, 6, 45);
        var checkedIn = await staff.PostOk($"{Api}/attendance/check-in", new { assignmentId = id });
        checkedIn.GetProperty("checkInAtUtc").GetDateTime().Should().Be(F.Clock.Now.UtcDateTime);
        await staff.Post($"{Api}/attendance/check-in", new { assignmentId = id }).ShouldBe(409, "attendance.already_checked_in");
    }

    [Fact]
    public async Task Two_attendance_actions_must_be_5_minutes_apart_BR50_and_check_out_needs_a_check_in()
    {
        var (userId, employee) = await NewEmployeeAsync("An");
        var shift = await NewShiftAsync("Sáng", "07:00", "12:00");
        var id = await AssignAsync(employee, shift, Monday);
        var staff = Staff(userId);

        SetLocal(Monday, 7, 0);
        await staff.Post($"{Api}/attendance/check-out", new { assignmentId = id }).ShouldBe(422, "attendance.not_checked_in");
        await staff.PostOk($"{Api}/attendance/check-in", new { assignmentId = id });
        SetLocal(Monday, 7, 3);
        await staff.Post($"{Api}/attendance/check-out", new { assignmentId = id }).ShouldBe(422, "attendance.too_soon");
        SetLocal(Monday, 12, 5);
        var done = await staff.PostOk($"{Api}/attendance/check-out", new { assignmentId = id });
        done.GetProperty("checkOutAtUtc").GetDateTime().Should().Be(F.Clock.Now.UtcDateTime);
        await staff.Post($"{Api}/attendance/check-out", new { assignmentId = id }).ShouldBe(409, "attendance.already_checked_out");
    }

    [Fact]
    public async Task Nobody_checks_in_for_someone_else_or_for_a_declined_or_missing_shift_BR49()
    {
        var (userA, employeeA) = await NewEmployeeAsync("An");
        var (userB, _) = await NewEmployeeAsync("Bình");
        var shift = await NewShiftAsync("Sáng", "07:00", "12:00");
        var id = await AssignAsync(employeeA, shift, Monday);
        SetLocal(Monday, 7, 0);

        await Staff(userB).Post($"{Api}/attendance/check-in", new { assignmentId = id }).ShouldBe(404);
        await Staff(userA).Post($"{Api}/attendance/check-in", new { }).ShouldBe(400);
        await Staff(userA).Post($"{Api}/attendance/check-in", new { assignmentId = Guid.NewGuid() }).ShouldBe(404);
        await F.As(AicosRoles.Cashier, B1).Post($"{Api}/attendance/check-in", new { assignmentId = id }).ShouldBe(404);      // no profile for this account
        await F.As(AicosRoles.Customer).Post($"{Api}/attendance/check-in", new { assignmentId = id }).ShouldBe(403);

        await Staff(userA).PostOk($"{Api}/shift-assignments/{id}/decline", new { reason = "Ốm" });
        await Staff(userA).Post($"{Api}/attendance/check-in", new { assignmentId = id }).ShouldBe(422, "attendance.shift_not_active");
    }
}
