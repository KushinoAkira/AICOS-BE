using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;

namespace AICOS.Services.IntegrationTests;

/// <summary>UC-30 open and approve a replacement shift, UC-31 register for it, with BR-51 (approval) and BR-52 (contract unchanged).</summary>
public sealed class WorkforceReplacementTests : WorkforceTestBase
{
    private const string Replacements = $"{Api}/replacement-shifts";

    private async Task<(Guid Shift, Guid Absent, Guid AbsentUser, Guid Assignment)> AbsentColleagueAsync(string contract = "PartTime")
    {
        var shift = await NewShiftAsync("Sáng", "07:00", "12:00");
        var (user, employee) = await NewEmployeeAsync("An Nghỉ", contract: contract);
        var assignment = await AssignAsync(employee, shift, Monday.AddDays(1));
        await Staff(user).PostOk($"{Api}/shift-assignments/{assignment}/decline", new { reason = "Ốm" });
        return (shift, employee, user, assignment);
    }

    private async Task<JsonElement> OpenAsync(Guid assignment, string reason = "An bị ốm") =>
        await Manager.PostOk(Replacements, new { assignmentId = assignment, reason });

    // ------------------------------------------------------------------ open (UC-30)
    [Fact]
    public async Task The_manager_opens_a_replacement_for_an_assignment_and_it_copies_the_shift()
    {
        var (_, _, _, assignment) = await AbsentColleagueAsync();

        var replacement = await OpenAsync(assignment);

        replacement.GetProperty("status").GetString().Should().Be("Open");
        replacement.GetProperty("workDate").GetString().Should().Be(D(Monday.AddDays(1)));
        replacement.GetProperty("startTime").GetString().Should().Be("07:00");
        replacement.GetProperty("hours").GetDouble().Should().Be(5);
        replacement.GetProperty("shiftName").GetString().Should().Be("Sáng");
        replacement.GetProperty("originalEmployeeName").GetString().Should().Be("An Nghỉ");
        replacement.GetProperty("applications").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task An_extra_shift_can_be_opened_from_a_template_and_a_date()
    {
        var shift = await NewShiftAsync("Chiều", "13:00", "18:00");

        var replacement = await Manager.PostOk(Replacements, new { workShiftId = shift, workDate = D(Monday.AddDays(2)), reason = "Đông khách" });

        replacement.GetProperty("originalAssignmentId").ValueKind.Should().Be(JsonValueKind.Null);
        replacement.GetProperty("shiftName").GetString().Should().Be("Chiều");
    }

    [Fact]
    public async Task Opening_input_is_validated_and_a_worked_replaced_or_past_shift_cannot_be_opened()
    {
        var (shift, employee, user, assignment) = await AbsentColleagueAsync();
        var worked = await AssignAsync(employee, shift, Monday);
        SetLocal(Monday, 7, 0);
        await Staff(user).PostOk($"{Api}/attendance/check-in", new { assignmentId = worked });

        await Manager.Post(Replacements, new { reason = "x" }).ShouldBe(400);                                                                    // neither
        await Manager.Post(Replacements, new { assignmentId = assignment, workShiftId = shift, workDate = D(Monday.AddDays(1)), reason = "x" }).ShouldBe(400); // both
        await Manager.Post(Replacements, new { assignmentId = assignment }).ShouldBe(400);                                                       // reason required
        await Manager.Post(Replacements, new { workShiftId = shift, reason = "x" }).ShouldBe(400);
        await Manager.Post(Replacements, new { assignmentId = worked, reason = "x" }).ShouldBe(422, "replacement.shift_worked");
        await Manager.Post(Replacements, new { assignmentId = Guid.NewGuid(), reason = "x" }).ShouldBe(404);
        await Manager.Post(Replacements, new { workShiftId = shift, workDate = D(Monday.AddDays(61)), reason = "x" }).ShouldBe(400);

        SetLocal(Monday.AddDays(1), 13, 0);
        await Manager.Post(Replacements, new { assignmentId = assignment, reason = "x" }).ShouldBe(422, "replacement.shift_over");
        await Manager.Post(Replacements, new { workShiftId = shift, workDate = D(Monday.AddDays(1)), reason = "x" }).ShouldBe(422, "replacement.shift_over");
    }

    [Fact]
    public async Task A_shift_can_only_have_one_open_replacement_until_it_is_cancelled()
    {
        var (_, _, _, assignment) = await AbsentColleagueAsync();
        var first = await OpenAsync(assignment);

        await Manager.Post(Replacements, new { assignmentId = assignment, reason = "again" }).ShouldBe(409, "replacement.duplicate");
        await Manager.PostOk($"{Replacements}/{first.Id()}/cancel", new { note = "Đã có người" });
        await Manager.Post(Replacements, new { assignmentId = assignment, reason = "again" }).ShouldBe(201);
    }

    // ------------------------------------------------------------------ register (UC-31)
    [Fact]
    public async Task Colleagues_of_the_branch_see_the_open_shift_register_and_withdraw()
    {
        var (_, _, _, assignment) = await AbsentColleagueAsync();
        var replacement = await OpenAsync(assignment);
        var (userId, _) = await NewEmployeeAsync("Bình Đi Làm");
        var (outsider, _) = await NewEmployeeAsync("Chi Chi Nhánh Hai", branch: B2);
        var colleague = Staff(userId);

        (await colleague.GetFromJsonAsync<JsonElement>(Replacements)).GetArrayLength().Should().Be(1);
        (await Staff(outsider, branch: B2).GetFromJsonAsync<JsonElement>(Replacements)).GetArrayLength().Should().Be(0);    // another branch sees nothing
        await Staff(outsider, branch: B2).Post($"{Replacements}/{replacement.Id()}/register", new { }).ShouldBe(403, "branch.forbidden");

        F.Events.Published.OfType<ReplacementShiftOpenedEvent>().Should().Contain(e => e.ReplacementId == replacement.Id());
        var registered = await colleague.PostOk($"{Replacements}/{replacement.Id()}/register", new { note = "Em làm được" });
        F.Events.Published.OfType<ReplacementRegisteredEvent>().Should().ContainSingle(e => e.ReplacementId == replacement.Id());
        registered.GetProperty("myApplication").GetString().Should().Be("Pending");
        registered.GetProperty("applications").GetInt32().Should().Be(1);
        await colleague.Post($"{Replacements}/{replacement.Id()}/register", new { }).ShouldBe(409, "replacement.already_registered");

        var withdrawn = await colleague.PostOk($"{Replacements}/{replacement.Id()}/withdraw", new { });
        withdrawn.GetProperty("myApplication").GetString().Should().Be("Withdrawn");
        withdrawn.GetProperty("applications").GetInt32().Should().Be(0);
        await colleague.Post($"{Replacements}/{replacement.Id()}/withdraw", new { }).ShouldBe(404);
        await colleague.PostOk($"{Replacements}/{replacement.Id()}/register", new { });                              // may register again after withdrawing
    }

    [Fact]
    public async Task Registration_rules_not_your_own_shift_no_overlap_weekly_cap_and_not_after_it_is_closed()
    {
        var (shift, employee, absentUser, assignment) = await AbsentColleagueAsync();
        var replacement = await OpenAsync(assignment);
        var (busyUser, busyEmp) = await NewEmployeeAsync("Bình Bận");
        var (freeUser, _) = await NewEmployeeAsync("Chi Rảnh");
        await AssignAsync(busyEmp, shift, Monday.AddDays(1));                                                          // already working that morning

        await Staff(absentUser).Post($"{Replacements}/{replacement.Id()}/register", new { }).ShouldBe(422, "replacement.own_shift");
        await Staff(busyUser).Post($"{Replacements}/{replacement.Id()}/register", new { }).ShouldBe(409, "shift.overlap");
        await F.As(AicosRoles.Cashier, B1).Post($"{Replacements}/{replacement.Id()}/register", new { }).ShouldBe(404);  // no employee profile
        await Manager.Post($"{Replacements}/{replacement.Id()}/register", new { }).ShouldBe(404);

        await Manager.PostOk($"{Replacements}/{replacement.Id()}/cancel", new { });
        await Staff(freeUser).Post($"{Replacements}/{replacement.Id()}/register", new { }).ShouldBe(422, "replacement.not_open");
    }

    // ------------------------------------------------------------------ approve (BR-51, BR-52)
    [Fact]
    public async Task Approving_a_volunteer_gives_them_the_shift_releases_the_original_and_rejects_the_others()
    {
        var (_, _, _, assignment) = await AbsentColleagueAsync();
        var replacement = await OpenAsync(assignment);
        var (chosenUser, chosenEmp) = await NewEmployeeAsync("Bình Thay Ca", AicosRoles.Barista, "FullTime");
        var (otherUser, _) = await NewEmployeeAsync("Chi Dự Phòng");
        await Staff(chosenUser).PostOk($"{Replacements}/{replacement.Id()}/register", new { });
        await Staff(otherUser).PostOk($"{Replacements}/{replacement.Id()}/register", new { });
        var applications = await Manager.GetFromJsonAsync<JsonElement>($"{Replacements}/{replacement.Id()}/applications");
        applications.GetArrayLength().Should().Be(2);
        var chosen = applications.EnumerateArray().Single(a => a.GetProperty("employeeName").GetString() == "Bình Thay Ca");
        chosen.GetProperty("contractType").GetString().Should().Be("FullTime");

        var approved = await Manager.PostOk($"{Replacements}/{replacement.Id()}/approve", new { applicationId = chosen.Id(), note = "Chọn Bình" });

        approved.GetProperty("status").GetString().Should().Be("Filled");
        approved.GetProperty("approvedEmployeeName").GetString().Should().Be("Bình Thay Ca");
        var newAssignment = await GetAssignmentAsync(approved.GetProperty("approvedAssignmentId").GetGuid());
        newAssignment.GetProperty("employeeId").GetGuid().Should().Be(chosenEmp);
        newAssignment.GetProperty("isReplacement").GetBoolean().Should().BeTrue();
        newAssignment.GetProperty("status").GetString().Should().Be("Confirmed");                  // the volunteer already agreed
        newAssignment.GetProperty("workDate").GetString().Should().Be(D(Monday.AddDays(1)));
        (await Manager.GetFromJsonAsync<JsonElement>($"{Api}/employees/{chosenEmp}")).GetProperty("contractType").GetString().Should().Be("FullTime"); // BR-52

        var after = await Manager.GetFromJsonAsync<JsonElement>($"{Replacements}/{replacement.Id()}/applications");
        after.EnumerateArray().Select(a => a.GetProperty("status").GetString()).Should().BeEquivalentTo("Approved", "Rejected");
        (await Staff(otherUser).GetFromJsonAsync<JsonElement>(Replacements)).GetArrayLength().Should().Be(0);   // no longer open
    }

    [Fact]
    public async Task A_confirmed_assignment_is_marked_replaced_when_somebody_takes_over_and_is_not_paid()
    {
        var shift = await NewShiftAsync("Sáng", "07:00", "12:00");
        var (_, employee) = await NewEmployeeAsync("An Vắng");
        var assignment = await AssignAsync(employee, shift, Monday.AddDays(1));
        var (volunteer, _) = await NewEmployeeAsync("Bình");
        var replacement = await OpenAsync(assignment, "An xin nghỉ");
        await Staff(volunteer).PostOk($"{Replacements}/{replacement.Id()}/register", new { });
        var application = (await Manager.GetFromJsonAsync<JsonElement>($"{Replacements}/{replacement.Id()}/applications"))[0];

        await Manager.PostOk($"{Replacements}/{replacement.Id()}/approve", new { applicationId = application.Id() });

        (await GetAssignmentAsync(assignment)).GetProperty("status").GetString().Should().Be("Replaced");
        await Manager.Post($"{Replacements}", new { assignmentId = assignment, reason = "x" }).ShouldBe(422, "replacement.already_replaced");
        SetLocal(Monday.AddDays(1), 13, 0);
        await Manager.Post($"{Api}/attendance/{assignment}/approve", new { }).ShouldBe(422, "attendance.not_reviewable");
    }

    [Fact]
    public async Task Approval_is_audited_and_validated()
    {
        var (_, _, _, assignment) = await AbsentColleagueAsync();
        var replacement = await OpenAsync(assignment);
        var (userId, _) = await NewEmployeeAsync("Bình");
        await Staff(userId).PostOk($"{Replacements}/{replacement.Id()}/register", new { });
        var application = (await Manager.GetFromJsonAsync<JsonElement>($"{Replacements}/{replacement.Id()}/applications"))[0];
        var url = $"{Replacements}/{replacement.Id()}/approve";

        await Manager.Post(url, new { }).ShouldBe(400);
        await Manager.Post(url, new { applicationId = Guid.NewGuid() }).ShouldBe(404);
        await Manager.PostOk(url, new { applicationId = application.Id() });
        await Manager.Post(url, new { applicationId = application.Id() }).ShouldBe(422, "replacement.not_open");

        var audit = F.Events.Published.OfType<AuditRecordedEvent>().Single(e => e.Action == "shift.replacement_approved");
        audit.TenantId.Should().Be(Tenant);
        audit.EntityType.Should().Be("ReplacementShift");
        audit.ActorRole.Should().Be(AicosRoles.BranchManager);
        audit.After.Should().Contain("\"status\":\"Filled\"");
        JsonDocument.Parse(audit.After!).RootElement.GetProperty("replacementEmployee").GetString().Should().Be("Bình"); // JSON escapes accents
    }

    [Fact]
    public async Task Approval_rechecks_for_a_clash_that_appeared_after_the_volunteer_registered()
    {
        var (shift, _, _, assignment) = await AbsentColleagueAsync();
        var replacement = await OpenAsync(assignment);
        var (userId, employeeId) = await NewEmployeeAsync("Bình");
        await Staff(userId).PostOk($"{Replacements}/{replacement.Id()}/register", new { });
        var application = (await Manager.GetFromJsonAsync<JsonElement>($"{Replacements}/{replacement.Id()}/applications"))[0];
        await AssignAsync(employeeId, shift, Monday.AddDays(1));                                    // the manager scheduled Bình meanwhile

        await Manager.Post($"{Replacements}/{replacement.Id()}/approve", new { applicationId = application.Id() }).ShouldBe(409, "shift.overlap");
        (await Manager.GetFromJsonAsync<JsonElement>($"{Replacements}/{replacement.Id()}")).GetProperty("status").GetString().Should().Be("Open");
    }

    [Fact]
    public async Task Cancelling_closes_the_shift_and_rejects_the_registrations()
    {
        var (_, _, _, assignment) = await AbsentColleagueAsync();
        var replacement = await OpenAsync(assignment);
        var (userId, _) = await NewEmployeeAsync("Bình");
        await Staff(userId).PostOk($"{Replacements}/{replacement.Id()}/register", new { });

        var cancelled = await Manager.PostOk($"{Replacements}/{replacement.Id()}/cancel", new { note = "Không cần nữa" });

        cancelled.GetProperty("status").GetString().Should().Be("Cancelled");
        cancelled.GetProperty("managerNote").GetString().Should().Be("Không cần nữa");
        (await Manager.GetFromJsonAsync<JsonElement>($"{Replacements}/{replacement.Id()}/applications"))[0].GetProperty("status").GetString().Should().Be("Rejected");
        await Manager.Post($"{Replacements}/{replacement.Id()}/cancel", new { }).ShouldBe(422, "replacement.not_open");
    }

    [Fact]
    public async Task A_staff_member_asks_for_a_replacement_of_their_own_coming_shift_and_the_manager_still_picks_the_volunteer()
    {
        var shift = await NewShiftAsync("Chiều", "13:00", "18:00");
        var (ownerUser, owner) = await NewEmployeeAsync("Chi Bận");
        var assignment = await AssignAsync(owner, shift, Monday.AddDays(1));

        var opened = await Staff(ownerUser).PostOk(Replacements, new { assignmentId = assignment, reason = "Em có lịch thi" });
        opened.GetProperty("status").GetString().Should().Be("Open");
        opened.GetProperty("originalAssignmentId").GetGuid().Should().Be(assignment);
        await Staff(ownerUser).Post(Replacements, new { assignmentId = assignment, reason = "lại" }).ShouldBe(409, "replacement.duplicate");
        await Staff(ownerUser).Post($"{Replacements}/{opened.Id()}/register", new { }).ShouldBe(422, "replacement.own_shift");

        var (helperUser, _) = await NewEmployeeAsync("Dũng Rảnh");
        await Staff(helperUser).PostOk($"{Replacements}/{opened.Id()}/register", new { note = "Em làm được" });
        await Staff(helperUser).Post($"{Replacements}/{opened.Id()}/approve", new { }).ShouldBe(403);   // only the manager approves (BR-51)
        var application = (await Manager.GetFromJsonAsync<JsonElement>($"{Replacements}/{opened.Id()}/applications"))[0];
        (await Manager.PostOk($"{Replacements}/{opened.Id()}/approve", new { applicationId = application.Id() }))
            .GetProperty("status").GetString().Should().Be("Filled");
    }

    [Fact]
    public async Task The_requester_takes_back_their_own_request_while_nobody_is_approved_and_nobody_else_can()
    {
        var shift = await NewShiftAsync("Trưa", "10:00", "14:00");
        var (ownerUser, owner) = await NewEmployeeAsync("Hà Hết Bận");
        var assignment = await AssignAsync(owner, shift, Monday.AddDays(1));
        var request = await Staff(ownerUser).PostOk(Replacements, new { assignmentId = assignment, reason = "Bận" });
        var (helperUser, _) = await NewEmployeeAsync("Khánh");
        await Staff(helperUser).PostOk($"{Replacements}/{request.Id()}/register", new { });

        await Staff(helperUser).Post($"{Replacements}/{request.Id()}/retract", new { }).ShouldBe(403, "replacement.not_requester");
        var taken = await Staff(ownerUser).PostOk($"{Replacements}/{request.Id()}/retract", new { });
        taken.GetProperty("status").GetString().Should().Be("Cancelled");
        (await Manager.GetFromJsonAsync<JsonElement>($"{Replacements}/{request.Id()}/applications"))[0].GetProperty("status").GetString().Should().Be("Rejected");
        await Staff(ownerUser).Post($"{Replacements}/{request.Id()}/retract", new { }).ShouldBe(422, "replacement.not_open");

        // The shift is still theirs, and a new request can be made later.
        (await Staff(ownerUser).PostOk(Replacements, new { assignmentId = assignment, reason = "Lại bận" })).GetProperty("status").GetString().Should().Be("Open");
    }

    [Fact]
    public async Task Removing_a_shift_cancels_its_open_replacement_but_an_approved_one_keeps_it()
    {
        var shift = await NewShiftAsync("Tối", "18:00", "22:00");
        var (ownerUser, owner) = await NewEmployeeAsync("Em Nhờ");
        var asked = await AssignAsync(owner, shift, Monday.AddDays(1));
        var request = await Staff(ownerUser).PostOk(Replacements, new { assignmentId = asked, reason = "Bận" });
        var (helperUser, _) = await NewEmployeeAsync("Em Giúp");
        await Staff(helperUser).PostOk($"{Replacements}/{request.Id()}/register", new { });

        await Manager.DeleteAsync($"{Api}/shift-assignments/{asked}").ShouldBe(204);
        (await Manager.GetFromJsonAsync<JsonElement>($"{Replacements}/{request.Id()}")).GetProperty("status").GetString().Should().Be("Cancelled");
        (await Manager.GetFromJsonAsync<JsonElement>($"{Replacements}/{request.Id()}/applications"))[0].GetProperty("status").GetString().Should().Be("Rejected");

        var filled = await AssignAsync(owner, shift, Monday.AddDays(2));
        var second = await Staff(ownerUser).PostOk(Replacements, new { assignmentId = filled, reason = "Bận" });
        await Staff(helperUser).PostOk($"{Replacements}/{second.Id()}/register", new { });
        var application = (await Manager.GetFromJsonAsync<JsonElement>($"{Replacements}/{second.Id()}/applications"))[0];
        await Manager.PostOk($"{Replacements}/{second.Id()}/approve", new { applicationId = application.Id() });

        await Manager.DeleteAsync($"{Api}/shift-assignments/{filled}").ShouldBe(409, "shift.has_replacement");
    }

    // ------------------------------------------------------------------ who may do what
    [Fact]
    public async Task Only_managers_approve_and_cancel_staff_open_only_their_own_shift_and_other_branches_and_brands_are_kept_out()
    {
        var (_, _, _, assignment) = await AbsentColleagueAsync();
        var replacement = await OpenAsync(assignment);
        var (userId, _) = await NewEmployeeAsync("Bình");
        await Staff(userId).PostOk($"{Replacements}/{replacement.Id()}/register", new { });
        var application = (await Manager.GetFromJsonAsync<JsonElement>($"{Replacements}/{replacement.Id()}/applications"))[0];
        var staff = Staff(userId);

        await staff.Post(Replacements, new { assignmentId = assignment, reason = "x" }).ShouldBe(404);                               // not their shift
        await staff.Post(Replacements, new { workShiftId = Guid.NewGuid(), workDate = D(Monday.AddDays(1)), reason = "x" }).ShouldBe(403, "replacement.own_shift_only");
        await staff.Post($"{Replacements}/{replacement.Id()}/approve", new { applicationId = application.Id() }).ShouldBe(403);
        await staff.Post($"{Replacements}/{replacement.Id()}/cancel", new { }).ShouldBe(403);
        await staff.GetAsync($"{Replacements}/{replacement.Id()}/applications").ShouldBe(403);
        await F.As(AicosRoles.Customer).GetAsync(Replacements).ShouldBe(403);

        await Manager2.Post($"{Replacements}/{replacement.Id()}/approve", new { applicationId = application.Id() }).ShouldBe(403, "branch.forbidden");
        await Manager2.GetAsync($"{Replacements}/{replacement.Id()}").ShouldBe(403, "branch.forbidden");
        await Manager2.Post(Replacements, new { assignmentId = assignment, reason = "x" }).ShouldBe(403, "branch.forbidden");
        await OtherAdmin.GetAsync($"{Replacements}/{replacement.Id()}").ShouldBe(404);
        await OtherAdmin.Post($"{Replacements}/{replacement.Id()}/cancel", new { }).ShouldBe(404);
        (await OtherAdmin.GetFromJsonAsync<JsonElement>(Replacements)).GetArrayLength().Should().Be(0);
        (await Admin.GetFromJsonAsync<JsonElement>(Replacements)).GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Managers_see_every_status_while_staff_see_open_ones_by_default()
    {
        var (shift, _, _, assignment) = await AbsentColleagueAsync();
        var open = await OpenAsync(assignment);
        var cancelled = await Manager.PostOk(Replacements, new { workShiftId = shift, workDate = D(Monday.AddDays(2)), reason = "extra" });
        await Manager.PostOk($"{Replacements}/{cancelled.Id()}/cancel", new { });
        var (userId, _) = await NewEmployeeAsync("Bình");

        (await Manager.GetFromJsonAsync<JsonElement>(Replacements)).GetArrayLength().Should().Be(2);
        (await Manager.GetFromJsonAsync<JsonElement>($"{Replacements}?status=Cancelled")).GetArrayLength().Should().Be(1);
        (await Manager.GetFromJsonAsync<JsonElement>($"{Replacements}?from={D(Monday.AddDays(2))}")).GetArrayLength().Should().Be(1);
        var forStaff = await Staff(userId).GetFromJsonAsync<JsonElement>(Replacements);
        forStaff.GetArrayLength().Should().Be(1);
        forStaff[0].Id().Should().Be(open.Id());
    }
}
