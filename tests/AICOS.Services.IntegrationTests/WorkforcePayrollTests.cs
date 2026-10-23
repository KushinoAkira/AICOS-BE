using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;
using AICOS.WorkforceScheduling.Api.Domain;
using AICOS.WorkforceScheduling.Api.Features.Payroll;

namespace AICOS.Services.IntegrationTests;

/// <summary>UC-19 pay rules and UC-32 payroll summary and export. Pay follows BR-53 and only approved attendance counts (BR-54).</summary>
public sealed class WorkforcePayrollTests : WorkforceTestBase
{
    private const string Rules = $"{Api}/pay-rules";
    private const string Payroll = $"{Api}/payroll";

    private static object NewRules(decimal day = 300_000, decimal hour = 30_000, decimal replacement = 45_000, decimal otAfter = 8, decimal otMultiplier = 1.5m) => new
    {
        fullTimeDayRate = day, partTimeHourRate = hour, replacementHourRate = replacement, overtimeAfterHours = otAfter, overtimeMultiplier = otMultiplier,
    };

    private async Task WorkAsync(HttpClient staff, Guid assignment, DateOnly date, (int H, int M) inAt, (int H, int M) outAt)
    {
        SetLocal(date, inAt.H, inAt.M);
        await staff.PostOk($"{Api}/attendance/check-in", new { assignmentId = assignment });
        SetLocal(date, outAt.H, outAt.M);
        await staff.PostOk($"{Api}/attendance/check-out", new { assignmentId = assignment });
    }

    private Task<JsonElement> SummaryAsync(HttpClient? client = null, string extra = "") =>
        (client ?? Manager).GetFromJsonAsync<JsonElement>($"{Payroll}/summary?from={D(Monday)}&to={D(Monday.AddDays(6))}{extra}");

    private static JsonElement Line(JsonElement summary, string name) =>
        summary.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("employeeName").GetString() == name);

    // ------------------------------------------------------------------ pay rules (UC-19)
    [Fact]
    public async Task Pay_rules_are_unconfigured_until_the_brand_admin_saves_them_and_each_save_replaces_the_last()
    {
        (await Manager.GetFromJsonAsync<JsonElement>(Rules)).GetProperty("isConfigured").GetBoolean().Should().BeFalse();

        var saved = await Admin.Put(Rules, NewRules());
        saved.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        (await saved.Body()).GetProperty("isConfigured").GetBoolean().Should().BeTrue();
        var read = await Manager.GetFromJsonAsync<JsonElement>(Rules);
        read.GetProperty("fullTimeDayRate").GetDecimal().Should().Be(300_000);
        read.GetProperty("overtimeMultiplier").GetDecimal().Should().Be(1.5m);

        await Admin.Put(Rules, NewRules(day: 350_000)).ShouldBe(200);
        (await Admin.GetFromJsonAsync<JsonElement>(Rules)).GetProperty("fullTimeDayRate").GetDecimal().Should().Be(350_000);
        (await OtherAdmin.GetFromJsonAsync<JsonElement>(Rules)).GetProperty("isConfigured").GetBoolean().Should().BeFalse();   // another brand is not affected
    }

    [Fact]
    public async Task Pay_rules_are_validated_and_only_the_brand_admin_writes_them()
    {
        await Admin.Put(Rules, new { }).ShouldBe(400);
        await Admin.Put(Rules, NewRules(day: 0)).ShouldBe(400);
        await Admin.Put(Rules, NewRules(hour: -1)).ShouldBe(400);
        await Admin.Put(Rules, NewRules(replacement: 0)).ShouldBe(400);
        await Admin.Put(Rules, NewRules(otAfter: 0)).ShouldBe(400);
        await Admin.Put(Rules, NewRules(otAfter: 25)).ShouldBe(400);
        await Admin.Put(Rules, NewRules(otMultiplier: 0.9m)).ShouldBe(400);
        await Admin.Put(Rules, NewRules(otMultiplier: 6)).ShouldBe(400);
        await Admin.Put(Rules, NewRules(day: 100_000_001)).ShouldBe(400);

        await Manager.Put(Rules, NewRules()).ShouldBe(403);
        await F.As(AicosRoles.Cashier, B1).GetAsync(Rules).ShouldBe(403);
        await F.As(AicosRoles.SuperAdmin).GetAsync(Rules).ShouldBe(403);
        await F.CreateClient().GetAsync(Rules).ShouldBe(401);
    }

    [Fact]
    public async Task Changing_pay_rules_is_sent_to_the_audit_history()
    {
        await Admin.Put(Rules, NewRules()).ShouldBe(200);
        await Admin.Put(Rules, NewRules(hour: 35_000)).ShouldBe(200);

        var events = F.Events.Published.OfType<AuditRecordedEvent>().ToList();
        events.Select(e => e.Action).Should().Equal("pay_rules.updated", "pay_rules.updated");
        events[0].Before.Should().BeNull();
        events[1].Before.Should().Contain("\"partTimeHourRate\":30000");
        events[1].After.Should().Contain("\"partTimeHourRate\":35000");
        events.Should().OnlyContain(e => e.TenantId == Tenant);
    }

    // ------------------------------------------------------------------ payroll (UC-32)
    [Fact]
    public async Task Payroll_is_refused_until_the_brand_has_pay_rules_and_the_period_is_valid()
    {
        await Manager.GetAsync($"{Payroll}/summary?from={D(Monday)}&to={D(Monday)}").ShouldBe(422, "payroll.rules_missing");

        await Admin.Put(Rules, NewRules()).ShouldBe(200);
        await Manager.GetAsync($"{Payroll}/summary").ShouldBe(400);
        await Manager.GetAsync($"{Payroll}/summary?from={D(Monday)}").ShouldBe(400);
        await Manager.GetAsync($"{Payroll}/summary?from={D(Monday.AddDays(2))}&to={D(Monday)}").ShouldBe(400);
        await Manager.GetAsync($"{Payroll}/summary?from={D(Monday)}&to={D(Monday.AddDays(62))}").ShouldBe(400);
        (await SummaryAsync()).GetProperty("lines").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Full_time_staff_are_paid_per_day_part_time_per_hour_with_overtime_and_replacements_at_their_own_rate()
    {
        await Admin.Put(Rules, NewRules()).ShouldBe(200);
        var morning = await NewShiftAsync("Sáng", "07:00", "12:00");
        var long10 = await NewShiftAsync("Dài", "07:00", "17:00");
        var (ftUser, ft) = await NewEmployeeAsync("An Toàn Thời Gian", AicosRoles.Barista, "FullTime");
        var (ptUser, pt) = await NewEmployeeAsync("Bình Bán Thời Gian", AicosRoles.Cashier, "PartTime");
        var (absentUser, absentEmp) = await NewEmployeeAsync("Chi Nghỉ Ca");

        // Everything is scheduled first (Monday morning); then the week is worked in order.
        var ft1 = await AssignAsync(ft, morning, Monday);
        var ft2 = await AssignAsync(ft, morning, Monday.AddDays(1));
        var pt1 = await AssignAsync(pt, long10, Monday);
        var wed = Monday.AddDays(2);
        var wedShift = await NewShiftAsync("Chiều", "13:00", "16:00");
        var absent = await AssignAsync(absentEmp, wedShift, wed);

        // A third employee declines the Wednesday shift and the full-time employee takes it over: 3 replacement hours.
        await Staff(absentUser).PostOk($"{Api}/shift-assignments/{absent}/decline", new { reason = "Ốm" });
        var replacement = await Manager.PostOk($"{Api}/replacement-shifts", new { assignmentId = absent, reason = "Chi ốm" });
        await Staff(ftUser).PostOk($"{Api}/replacement-shifts/{replacement.Id()}/register", new { });
        var application = (await Manager.GetFromJsonAsync<JsonElement>($"{Api}/replacement-shifts/{replacement.Id()}/applications"))[0];
        var replaced = await Manager.PostOk($"{Api}/replacement-shifts/{replacement.Id()}/approve", new { applicationId = application.Id() });

        // Full-time: two normal days of 5 hours -> 2 x 300,000. Part-time: one 10-hour day -> 8 regular + 2 overtime hours.
        await WorkAsync(Staff(ftUser), ft1, Monday, (7, 0), (12, 0));
        await WorkAsync(Staff(ptUser, AicosRoles.Cashier), pt1, Monday, (7, 0), (17, 0));
        await WorkAsync(Staff(ftUser), ft2, Monday.AddDays(1), (7, 0), (12, 0));
        await WorkAsync(Staff(ftUser), replaced.GetProperty("approvedAssignmentId").GetGuid(), wed, (13, 0), (16, 0));

        SetLocal(Monday.AddDays(5), 9, 0);
        await Manager.PostOk($"{Api}/attendance/approve-range", new { from = D(Monday), to = D(Monday.AddDays(6)) });
        var summary = await SummaryAsync();

        var ftLine = Line(summary, "An Toàn Thời Gian");
        ftLine.GetProperty("workedDays").GetInt32().Should().Be(2);
        ftLine.GetProperty("basePay").GetDecimal().Should().Be(600_000);
        ftLine.GetProperty("replacementHours").GetDouble().Should().Be(3);
        ftLine.GetProperty("replacementPay").GetDecimal().Should().Be(135_000);          // 3 h x 45,000, contract type unchanged (BR-52)
        ftLine.GetProperty("totalPay").GetDecimal().Should().Be(735_000);

        var ptLine = Line(summary, "Bình Bán Thời Gian");
        ptLine.GetProperty("regularHours").GetDouble().Should().Be(8);
        ptLine.GetProperty("overtimeHours").GetDouble().Should().Be(2);
        ptLine.GetProperty("basePay").GetDecimal().Should().Be(240_000);                // 8 h x 30,000
        ptLine.GetProperty("overtimePay").GetDecimal().Should().Be(90_000);             // 2 h x 30,000 x 1.5
        ptLine.GetProperty("totalPay").GetDecimal().Should().Be(330_000);

        var absentLine = Line(summary, "Chi Nghỉ Ca");                                   // declined: not paid
        absentLine.GetProperty("totalPay").GetDecimal().Should().Be(0);

        var totals = summary.GetProperty("totals");
        totals.GetProperty("totalPay").GetDecimal().Should().Be(1_065_000);
        totals.GetProperty("basePay").GetDecimal().Should().Be(840_000);
        totals.GetProperty("overtimePay").GetDecimal().Should().Be(90_000);
        totals.GetProperty("replacementPay").GetDecimal().Should().Be(135_000);
        summary.GetProperty("rules").GetProperty("partTimeHourRate").GetDecimal().Should().Be(30_000);
    }

    [Fact]
    public async Task Unreviewed_records_are_not_paid_and_are_counted_absences_earn_nothing_BR54()
    {
        await Admin.Put(Rules, NewRules()).ShouldBe(200);
        var shift = await NewShiftAsync("Sáng", "07:00", "12:00");
        var (user, employee) = await NewEmployeeAsync("An", AicosRoles.Barista, "PartTime");
        var done = await AssignAsync(employee, shift, Monday);
        var pending = await AssignAsync(employee, shift, Monday.AddDays(1));
        var skipped = await AssignAsync(employee, shift, Monday.AddDays(2));
        await WorkAsync(Staff(user), done, Monday, (7, 0), (12, 0));
        await WorkAsync(Staff(user), pending, Monday.AddDays(1), (7, 0), (12, 0));
        SetLocal(Monday.AddDays(3), 9, 0);
        await Manager.PostOk($"{Api}/attendance/{done}/approve", new { });
        await Manager.PostOk($"{Api}/attendance/{skipped}/absent", new { reason = "Nghỉ" });

        var line = Line(await SummaryAsync(), "An");

        line.GetProperty("regularHours").GetDouble().Should().Be(5);                      // only the approved day
        line.GetProperty("basePay").GetDecimal().Should().Be(150_000);
        line.GetProperty("unreviewedRecords").GetInt32().Should().Be(1);                  // the 2nd day waits for the manager
        line.GetProperty("absences").GetInt32().Should().Be(1);
        (await SummaryAsync()).GetProperty("totals").GetProperty("unreviewedRecords").GetInt32().Should().Be(1);

        await Manager.PostOk($"{Api}/attendance/{pending}/approve", new { });
        Line(await SummaryAsync(), "An").GetProperty("basePay").GetDecimal().Should().Be(300_000);
    }

    [Fact]
    public async Task A_corrected_record_is_paid_by_the_corrected_times()
    {
        await Admin.Put(Rules, NewRules()).ShouldBe(200);
        var shift = await NewShiftAsync("Sáng", "07:00", "12:00");
        var (user, employee) = await NewEmployeeAsync("An", AicosRoles.Barista, "PartTime");
        var id = await AssignAsync(employee, shift, Monday);
        SetLocal(Monday, 7, 0);
        await Staff(user).PostOk($"{Api}/attendance/check-in", new { assignmentId = id });          // forgot to check out
        SetLocal(Monday, 13, 0);
        await Manager.PostOk($"{Api}/attendance/{id}/adjust", new { checkOutAt = new DateTimeOffset(Monday.ToDateTime(new TimeOnly(11, 0)), TimeSpan.FromHours(7)), reason = "Về lúc 11h" });

        var line = Line(await SummaryAsync(), "An");

        line.GetProperty("regularHours").GetDouble().Should().Be(4);
        line.GetProperty("basePay").GetDecimal().Should().Be(120_000);
    }

    [Fact]
    public async Task Payroll_is_scoped_a_manager_sees_their_branch_the_admin_the_brand_and_others_nothing()
    {
        await Admin.Put(Rules, NewRules()).ShouldBe(200);
        var shift1 = await NewShiftAsync("Sáng", "07:00", "12:00");
        var shift2 = await NewShiftAsync("Sáng", "07:00", "12:00", B2);
        var (u1, e1) = await NewEmployeeAsync("An Quận 1");
        var (u2, e2) = await NewEmployeeAsync("Bình Quận 3", branch: B2);
        var a1 = await AssignAsync(e1, shift1, Monday);
        var a2 = await AssignAsync(e2, shift2, Monday, Manager2);
        await WorkAsync(Staff(u1), a1, Monday, (7, 0), (12, 0));
        await WorkAsync(Staff(u2, branch: B2), a2, Monday, (7, 0), (12, 0));
        SetLocal(Monday, 18, 0);
        await Manager.PostOk($"{Api}/attendance/approve-range", new { from = D(Monday), to = D(Monday) });
        await Manager2.PostOk($"{Api}/attendance/approve-range", new { from = D(Monday), to = D(Monday) });

        (await SummaryAsync(Manager)).GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("employeeName").GetString()).Should().Equal("An Quận 1");
        (await SummaryAsync(Manager2)).GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("employeeName").GetString()).Should().Equal("Bình Quận 3");
        (await SummaryAsync(Admin)).GetProperty("lines").GetArrayLength().Should().Be(2);
        (await SummaryAsync(Admin, $"&branchId={B2}")).GetProperty("lines").GetArrayLength().Should().Be(1);
        (await SummaryAsync(Admin)).GetProperty("totals").GetProperty("totalPay").GetDecimal().Should().Be(300_000);
        await Manager.GetAsync($"{Payroll}/summary?from={D(Monday)}&to={D(Monday)}&branchId={B2}").ShouldBe(403, "branch.forbidden");
        await F.As(AicosRoles.Cashier, B1).GetAsync($"{Payroll}/summary?from={D(Monday)}&to={D(Monday)}").ShouldBe(403);
        await OtherAdmin.GetAsync($"{Payroll}/summary?from={D(Monday)}&to={D(Monday)}").ShouldBe(422, "payroll.rules_missing");   // their own brand has no rules
    }

    // ------------------------------------------------------------------ export
    [Fact]
    public async Task The_export_is_a_csv_that_excel_reads_and_that_cannot_run_a_formula()
    {
        await Admin.Put(Rules, NewRules()).ShouldBe(200);
        var shift = await NewShiftAsync("Sáng", "07:00", "12:00");
        var userId = NewAccount(AicosRoles.Barista, B1, "=HYPERLINK(\"http://evil\")");
        var employee = (await Manager.PostOk($"{Api}/employees", new { userId, contractType = "FullTime" })).Id();
        var id = await AssignAsync(employee, shift, Monday);
        await WorkAsync(Staff(userId), id, Monday, (7, 0), (12, 0));
        SetLocal(Monday, 18, 0);
        await Manager.PostOk($"{Api}/attendance/{id}/approve", new { });

        var response = await Manager.GetAsync($"{Payroll}/export?from={D(Monday)}&to={D(Monday.AddDays(6))}");

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
        response.Content.Headers.ContentDisposition!.FileName.Should().Be($"payroll-{Monday:yyyyMMdd}-{Monday.AddDays(6):yyyyMMdd}.csv");
        var bytes = await response.Content.ReadAsByteArrayAsync();
        bytes.Take(3).Should().Equal(0xEF, 0xBB, 0xBF);                              // UTF-8 byte-order mark
        var lines = Encoding.UTF8.GetString(bytes).TrimStart('﻿').Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        lines[0].Should().StartWith("Employee,Contract,Worked days");
        lines[1].Should().StartWith("\"'=HYPERLINK(\"\"http://evil\"\")\",FullTime,1,5,0,0,0,0,300000,0,0,0,300000");
        lines[2].Should().StartWith("TOTAL").And.EndWith("300000,0,0,0,300000");

        await Manager.GetAsync($"{Payroll}/export").ShouldBe(400);
    }

    // ------------------------------------------------------------------ the calculation on its own
    private static Employee EmployeeOf(ContractType type)
    {
        var e = new Employee(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Test", AicosRoles.Barista, null, null, type);
        return e;
    }

    private static ShiftAssignment Worked(Guid tenant, Employee employee, DateOnly date, int inHour, int outHour, bool replacement = false, bool approved = true)
    {
        var shift = new WorkShift(tenant, employee.BranchId, "S", new TimeOnly(7, 0), new TimeOnly(19, 0));
        var assignment = replacement
            ? new ShiftAssignment(tenant, employee.BranchId, employee.Id, shift.Id, date, new TimeOnly(inHour, 0), new TimeOnly(outHour, 0), Guid.NewGuid())
            : new ShiftAssignment(tenant, employee.BranchId, employee.Id, shift, date);
        assignment.CheckIn(date.ToDateTime(new TimeOnly(inHour, 0), DateTimeKind.Utc));
        assignment.CheckOut(date.ToDateTime(new TimeOnly(outHour, 0), DateTimeKind.Utc));
        if (approved)
        {
            assignment.ApproveAsRecorded(null, null, DateTime.UtcNow);
        }

        return assignment;
    }

    private static readonly PayRule Rule = new(Guid.NewGuid(), 300_000, 30_000, 45_000, 8, 1.5m);

    [Fact]
    public void The_calculator_pays_full_time_the_day_rate_plus_overtime_beyond_the_daily_threshold()
    {
        var e = EmployeeOf(ContractType.FullTime);
        var records = new[] { Worked(Tenant, e, Monday, 7, 9), Worked(Tenant, e, Monday.AddDays(1), 7, 17) };

        var line = PayrollCalculator.Calculate(e, records, Rule, _ => true);

        line.WorkedDays.Should().Be(2);
        line.BasePay.Should().Be(600_000);
        line.OvertimeHours.Should().Be(2);                 // the 10-hour day
        line.OvertimePay.Should().Be(112_500);             // 2 h x (300,000 / 8) x 1.5
        line.TotalPay.Should().Be(712_500);
    }

    [Fact]
    public void The_calculator_counts_a_full_time_day_once_even_with_two_shifts_that_day()
    {
        var e = EmployeeOf(ContractType.FullTime);
        var records = new[] { Worked(Tenant, e, Monday, 7, 11), Worked(Tenant, e, Monday, 13, 17) };

        PayrollCalculator.Calculate(e, records, Rule, _ => true).BasePay.Should().Be(300_000);
    }

    [Fact]
    public void The_calculator_pays_part_time_by_the_hour_and_multiplies_hours_beyond_the_daily_threshold()
    {
        var e = EmployeeOf(ContractType.PartTime);
        var records = new[] { Worked(Tenant, e, Monday, 7, 17), Worked(Tenant, e, Monday.AddDays(1), 8, 12) };   // 10 h and 4 h

        var line = PayrollCalculator.Calculate(e, records, Rule, _ => true);

        line.RegularHours.Should().Be(12);
        line.OvertimeHours.Should().Be(2);
        line.BasePay.Should().Be(360_000);
        line.OvertimePay.Should().Be(90_000);
        line.TotalPay.Should().Be(450_000);
    }

    [Fact]
    public void The_calculator_never_pays_unapproved_absent_declined_or_unfinished_records()
    {
        var e = EmployeeOf(ContractType.PartTime);
        var approved = Worked(Tenant, e, Monday, 7, 12);
        var unapproved = Worked(Tenant, e, Monday.AddDays(1), 7, 12, approved: false);
        var absent = Worked(Tenant, e, Monday.AddDays(2), 7, 12, approved: false);
        absent.MarkAbsent("Nghỉ", null, DateTime.UtcNow);
        var declined = new ShiftAssignment(Tenant, e.BranchId, e.Id, new WorkShift(Tenant, e.BranchId, "S", new TimeOnly(7, 0), new TimeOnly(12, 0)), Monday.AddDays(3));
        declined.Decline("Ốm");
        var shift = new WorkShift(Tenant, e.BranchId, "S", new TimeOnly(7, 0), new TimeOnly(12, 0));
        var unfinished = new ShiftAssignment(Tenant, e.BranchId, e.Id, shift, Monday.AddDays(4));
        unfinished.CheckIn(Monday.AddDays(4).ToDateTime(new TimeOnly(7, 0), DateTimeKind.Utc));

        var line = PayrollCalculator.Calculate(e, [approved, unapproved, absent, declined, unfinished], Rule, _ => true);

        line.RegularHours.Should().Be(5);
        line.TotalPay.Should().Be(150_000);
        line.Absences.Should().Be(1);
        line.UnreviewedRecords.Should().Be(2);              // the unapproved one and the unfinished one, both of ended shifts
        PayrollCalculator.Calculate(e, [unapproved], Rule, _ => false).UnreviewedRecords.Should().Be(0);   // a shift that has not ended is not "waiting"
    }

    [Fact]
    public void The_calculator_rounds_money_to_whole_dong_and_pays_replacements_at_the_replacement_rate_for_both_contracts()
    {
        var partTime = EmployeeOf(ContractType.PartTime);
        var fullTime = EmployeeOf(ContractType.FullTime);
        var odd = new PayRule(Guid.NewGuid(), 333_333, 33_333.33m, 44_444.44m, 8, 1.25m);

        var pt = PayrollCalculator.Calculate(partTime, [Worked(Tenant, partTime, Monday, 7, 10), Worked(Tenant, partTime, Monday.AddDays(1), 7, 10, replacement: true)], odd, _ => true);
        var ft = PayrollCalculator.Calculate(fullTime, [Worked(Tenant, fullTime, Monday.AddDays(1), 7, 10, replacement: true)], odd, _ => true);

        pt.BasePay.Should().Be(100_000);                    // 3 h x 33,333.33 = 99,999.99 -> 100,000
        pt.ReplacementPay.Should().Be(133_333);             // 3 h x 44,444.44 = 133,333.32
        ft.WorkedDays.Should().Be(0);                       // a replacement shift is not a normal working day
        ft.BasePay.Should().Be(0);
        ft.ReplacementPay.Should().Be(133_333);
        ft.ContractType.Should().Be(ContractType.FullTime);
    }
}
