using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;

namespace AICOS.Services.IntegrationTests;

/// <summary>SCRUM-129: payroll adjustments (BR-56), overtime for full-time staff (BR-53) and the work-records export (FR-28). SCRUM-63: attendance is audited.</summary>
public sealed class WorkforcePayrollAdjustmentTests : WorkforceTestBase
{
    private const string Payroll = $"{Api}/payroll";
    private const string Adjustments = $"{Payroll}/adjustments";

    public WorkforcePayrollAdjustmentTests()
    {
        Admin.Put($"{Api}/pay-rules", new { fullTimeDayRate = 300_000, partTimeHourRate = 30_000, replacementHourRate = 45_000, overtimeAfterHours = 8, overtimeMultiplier = 1.5 })
            .ShouldBe(200).GetAwaiter().GetResult();
    }

    private async Task WorkAsync(HttpClient staff, Guid assignment, DateOnly date, int inHour, int outHour)
    {
        SetLocal(date, inHour);
        await staff.PostOk($"{Api}/attendance/check-in", new { assignmentId = assignment });
        SetLocal(date, outHour);
        await staff.PostOk($"{Api}/attendance/check-out", new { assignmentId = assignment });
    }

    private Task<JsonElement> SummaryAsync(HttpClient? client = null) =>
        (client ?? Manager).GetFromJsonAsync<JsonElement>($"{Payroll}/summary?from={D(Monday)}&to={D(Monday.AddDays(6))}");

    private static JsonElement Line(JsonElement summary, string name) =>
        summary.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("employeeName").GetString() == name);

    private static object Adj(Guid employee, DateOnly date, decimal amount = 50_000, string reason = "Thưởng chuyên cần") => new { employeeId = employee, workDate = D(date), amount, reason };

    // ------------------------------------------------------------------ full-time overtime
    [Fact]
    public async Task A_full_time_day_longer_than_eight_hours_earns_overtime_at_the_hourly_equivalent_of_the_day_rate()
    {
        var shift = await NewShiftAsync("Dài", "07:00", "17:00");
        var (user, employee) = await NewEmployeeAsync("An Toàn Thời Gian", AicosRoles.Barista, "FullTime");
        var assignment = await AssignAsync(employee, shift, Monday);
        await WorkAsync(Staff(user), assignment, Monday, 7, 17);
        SetLocal(Monday.AddDays(1), 9);
        await Manager.PostOk($"{Api}/attendance/approve-range", new { from = D(Monday), to = D(Monday) });

        var line = Line(await SummaryAsync(), "An Toàn Thời Gian");

        line.GetProperty("regularHours").GetDouble().Should().Be(8);
        line.GetProperty("overtimeHours").GetDouble().Should().Be(2);
        line.GetProperty("basePay").GetDecimal().Should().Be(300_000);
        line.GetProperty("overtimePay").GetDecimal().Should().Be(112_500);        // 2 h x (300,000 / 8) x 1.5
        line.GetProperty("totalPay").GetDecimal().Should().Be(412_500);
    }

    // ------------------------------------------------------------------ adjustments
    [Fact]
    public async Task A_bonus_and_a_deduction_change_the_total_of_the_period_they_fall_in()
    {
        var (_, employee) = await NewEmployeeAsync("Bình Bán Thời Gian", AicosRoles.Cashier);
        SetLocal(Monday.AddDays(3), 9);

        var bonus = await Manager.Post(Adjustments, Adj(employee, Monday, 50_000, "Thưởng"));
        bonus.StatusCode.Should().Be(System.Net.HttpStatusCode.Created);
        await Manager.PostOk(Adjustments, Adj(employee, Monday.AddDays(1), -20_000, "Làm vỡ ly"));
        SetLocal(Monday.AddDays(12), 9);
        await Manager.PostOk(Adjustments, Adj(employee, Monday.AddDays(10), 99_000, "Another period"));

        var summary = await SummaryAsync();

        var line = Line(summary, "Bình Bán Thời Gian");                           // no shift at all: an adjustment alone puts them on the payroll
        line.GetProperty("adjustments").GetDecimal().Should().Be(30_000);
        line.GetProperty("totalPay").GetDecimal().Should().Be(30_000);
        summary.GetProperty("totals").GetProperty("adjustments").GetDecimal().Should().Be(30_000);
        summary.GetProperty("totals").GetProperty("totalPay").GetDecimal().Should().Be(30_000);

        var listed = await Manager.GetFromJsonAsync<JsonElement>($"{Adjustments}?from={D(Monday)}&to={D(Monday.AddDays(6))}");
        listed.GetArrayLength().Should().Be(2);
        listed[0].GetProperty("employeeName").GetString().Should().Be("Bình Bán Thời Gian");
        (await Manager.GetFromJsonAsync<JsonElement>($"{Adjustments}?from={D(Monday)}&to={D(Monday.AddDays(6))}&employeeId={Guid.NewGuid()}")).GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Adjustments_appear_in_the_pay_export()
    {
        var (_, employee) = await NewEmployeeAsync("Chi Có Thưởng", AicosRoles.Cashier);
        SetLocal(Monday.AddDays(3), 9);
        await Manager.PostOk(Adjustments, Adj(employee, Monday, 70_000));

        var csv = await Manager.GetStringAsync($"{Payroll}/export?from={D(Monday)}&to={D(Monday.AddDays(6))}");

        csv.Should().Contain("Replacement pay,Adjustments,Total pay");
        csv.Split('\n').Should().Contain(l => l.StartsWith("\"Chi Có Thưởng\"") && l.TrimEnd().EndsWith(",70000,70000"));
    }

    [Fact]
    public async Task An_adjustment_is_removed_by_the_manager_and_the_total_goes_back()
    {
        var (_, employee) = await NewEmployeeAsync("Dũng", AicosRoles.Cashier);
        SetLocal(Monday.AddDays(3), 9);
        var adjustment = await Manager.PostOk(Adjustments, Adj(employee, Monday, 50_000));

        await Manager.DeleteAsync($"{Adjustments}/{adjustment.Id()}").ShouldBe(204);
        await Manager.DeleteAsync($"{Adjustments}/{adjustment.Id()}").ShouldBe(404);

        (await SummaryAsync()).GetProperty("lines").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Adjustments_are_validated()
    {
        var (_, employee) = await NewEmployeeAsync("Em", AicosRoles.Cashier);
        SetLocal(Monday.AddDays(3), 9);

        await Manager.Post(Adjustments, new { }).ShouldBe(400);
        await Manager.Post(Adjustments, Adj(employee, Monday, 0)).ShouldBe(400);
        await Manager.Post(Adjustments, Adj(employee, Monday, 50_000_001)).ShouldBe(400);
        await Manager.Post(Adjustments, Adj(employee, Monday, -50_000_001)).ShouldBe(400);
        await Manager.Post(Adjustments, Adj(employee, Monday, 10.5m)).ShouldBe(400);
        await Manager.Post(Adjustments, Adj(employee, Monday, 1000, "")).ShouldBe(400);
        await Manager.Post(Adjustments, Adj(employee, Monday.AddDays(30), 1000)).ShouldBe(400);                      // the future
        await Manager.Post(Adjustments, Adj(Guid.NewGuid(), Monday, 1000)).ShouldBe(404);
        await Manager.GetAsync(Adjustments).ShouldBe(400);                                                           // the period is required
        await Manager.PostOk(Adjustments, Adj(employee, Monday, 50_000_000));                                        // the limit itself is fine
    }

    [Fact]
    public async Task Adjustments_follow_branch_and_brand_boundaries_and_roles()
    {
        var (_, mine) = await NewEmployeeAsync("Mine", AicosRoles.Cashier);
        var (_, theirs) = await NewEmployeeAsync("Theirs", AicosRoles.Cashier, branch: B2);
        SetLocal(Monday.AddDays(3), 9);

        await Manager.Post(Adjustments, Adj(theirs, Monday)).ShouldBe(403, "branch.forbidden");
        await Manager2.Post(Adjustments, Adj(mine, Monday)).ShouldBe(403, "branch.forbidden");
        await OtherAdmin.Post(Adjustments, Adj(mine, Monday)).ShouldBe(404);
        await Admin.Post(Adjustments, Adj(theirs, Monday)).ShouldBe(201);
        var row = await Manager.PostOk(Adjustments, Adj(mine, Monday));

        await Manager2.DeleteAsync($"{Adjustments}/{row.Id()}").ShouldBe(403, "branch.forbidden");
        await OtherAdmin.DeleteAsync($"{Adjustments}/{row.Id()}").ShouldBe(404);
        (await Manager.GetFromJsonAsync<JsonElement>($"{Adjustments}?from={D(Monday)}&to={D(Monday)}")).GetArrayLength().Should().Be(1);
        (await Admin.GetFromJsonAsync<JsonElement>($"{Adjustments}?from={D(Monday)}&to={D(Monday)}")).GetArrayLength().Should().Be(2);
        (await OtherAdmin.GetFromJsonAsync<JsonElement>($"{Adjustments}?from={D(Monday)}&to={D(Monday)}")).GetArrayLength().Should().Be(0);

        await F.As(AicosRoles.Cashier, B1).Post(Adjustments, Adj(mine, Monday)).ShouldBe(403);
        await F.As(AicosRoles.Barista, B1).GetAsync($"{Adjustments}?from={D(Monday)}&to={D(Monday)}").ShouldBe(403);
        await F.As(AicosRoles.SuperAdmin).GetAsync($"{Adjustments}?from={D(Monday)}&to={D(Monday)}").ShouldBe(403);
        await F.CreateClient().GetAsync(Adjustments).ShouldBe(401);
    }

    [Fact]
    public async Task Adding_and_removing_an_adjustment_go_to_the_audit_history()
    {
        var (_, employee) = await NewEmployeeAsync("Em", AicosRoles.Cashier);
        SetLocal(Monday.AddDays(3), 9);
        var row = await Manager.PostOk(Adjustments, Adj(employee, Monday, -15_000, "Đi trễ"));
        await Manager.DeleteAsync($"{Adjustments}/{row.Id()}").ShouldBe(204);

        var events = F.Events.Published.OfType<AuditRecordedEvent>().Where(e => e.EntityType == "PayrollAdjustment").ToList();

        events.Select(e => e.Action).Should().Equal("payroll_adjustment.added", "payroll_adjustment.removed");
        events[0].After.Should().Contain("-15000").And.Contain("workDate");
        events[1].Before.Should().Contain("-15000");
        events.Should().OnlyContain(e => e.TenantId == Tenant && e.EntityId == row.Id().ToString());
    }

    // ------------------------------------------------------------------ work records export
    [Fact]
    public async Task The_work_records_export_lists_every_scheduled_shift_with_local_times_and_whether_it_is_paid()
    {
        var shift = await NewShiftAsync("Sáng", "07:00", "12:00");
        var (user, employee) = await NewEmployeeAsync("=Nguy Hiểm", AicosRoles.Barista, "FullTime");
        var worked = await AssignAsync(employee, shift, Monday);
        await AssignAsync(employee, shift, Monday.AddDays(1));
        await WorkAsync(Staff(user), worked, Monday, 7, 12);
        SetLocal(Monday.AddDays(3), 9);
        await Manager.PostOk($"{Api}/attendance/approve-range", new { from = D(Monday), to = D(Monday) });

        var response = await Manager.GetAsync($"{Payroll}/records/export?from={D(Monday)}&to={D(Monday.AddDays(6))}");

        response.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
        var lines = (await response.Content.ReadAsStringAsync()).TrimStart('﻿').Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
        lines[0].Should().StartWith("Date,Employee,Shift start,Shift end");
        lines.Should().HaveCount(3);
        lines[1].Should().Be($"{D(Monday)},\"'=Nguy Hiểm\",07:00,12:00,Assigned,no,{D(Monday)} 07:00,{D(Monday)} 12:00,5,no,Approved,yes");
        lines[2].Should().EndWith(",no,,,0,no,Unreviewed,no");
        await Manager.GetAsync($"{Payroll}/records/export?from={D(Monday)}").ShouldBe(400);
        await Manager2.GetAsync($"{Payroll}/records/export?from={D(Monday)}&to={D(Monday)}&branchId={B1}").ShouldBe(403, "branch.forbidden");
        (await OtherAdmin.GetStringAsync($"{Payroll}/records/export?from={D(Monday)}&to={D(Monday)}")).Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(1);
        await F.As(AicosRoles.Cashier, B1).GetAsync($"{Payroll}/records/export?from={D(Monday)}&to={D(Monday)}").ShouldBe(403);
    }

    // ------------------------------------------------------------------ attendance audit (SCRUM-63)
    [Fact]
    public async Task Check_in_and_check_out_are_recorded_in_the_audit_history()
    {
        var shift = await NewShiftAsync("Sáng", "07:00", "12:00");
        var (user, employee) = await NewEmployeeAsync("Em", AicosRoles.Cashier);
        var assignment = await AssignAsync(employee, shift, Monday);

        await WorkAsync(Staff(user, AicosRoles.Cashier), assignment, Monday, 7, 12);

        var events = F.Events.Published.OfType<AuditRecordedEvent>().Where(e => e.EntityType == "ShiftAssignment").ToList();
        events.Select(e => e.Action).Should().Equal("attendance.checked_in", "attendance.checked_out");
        events.Should().OnlyContain(e => e.TenantId == Tenant && e.EntityId == assignment.ToString() && e.ActorRole == AicosRoles.Cashier);
    }
}
