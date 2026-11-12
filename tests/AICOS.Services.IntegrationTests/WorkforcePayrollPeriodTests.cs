using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;

namespace AICOS.Services.IntegrationTests;

/// <summary>Paying staff: finalize a payroll period, lock its days, record payouts, reopen, the bank transfer list and the payslips.</summary>
public sealed class WorkforcePayrollPeriodTests : WorkforceTestBase
{
    private const string Periods = $"{Api}/payroll/periods";
    private const string Adjustments = $"{Api}/payroll/adjustments";
    private static readonly DateOnly Sunday = Monday.AddDays(6);

    public WorkforcePayrollPeriodTests()
    {
        Admin.Put($"{Api}/pay-rules", new { fullTimeDayRate = 300_000, partTimeHourRate = 30_000, replacementHourRate = 45_000, overtimeAfterHours = 8, overtimeMultiplier = 1.5 })
            .ShouldBe(200).GetAwaiter().GetResult();
    }

    /// <summary>A part-time employee who worked Monday 07:00-11:00 (4 h x 30,000 = 120,000), not yet reviewed.</summary>
    private async Task<(Guid User, Guid Employee, Guid Assignment)> WorkedAsync(string name)
    {
        SetLocal(Monday, 6);
        var shift = await NewShiftAsync($"Sáng {name}", "07:00", "11:00");
        var (user, employee) = await NewEmployeeAsync(name);
        var assignment = await AssignAsync(employee, shift, Monday);
        SetLocal(Monday, 7);
        await Staff(user).PostOk($"{Api}/attendance/check-in", new { assignmentId = assignment });
        SetLocal(Monday, 11);
        await Staff(user).PostOk($"{Api}/attendance/check-out", new { assignmentId = assignment });
        return (user, employee, assignment);
    }

    private async Task ApproveWeekAsync()
    {
        SetLocal(Monday.AddDays(1), 9);
        await Manager.PostOk($"{Api}/attendance/approve-range", new { from = D(Monday), to = D(Sunday) });
    }

    private Task<JsonElement> FinalizeAsync(DateOnly? from = null, DateOnly? to = null)
    {
        SetLocal(Monday.AddDays(7), 9);
        return Manager.PostOk(Periods, new { from = D(from ?? Monday), to = D(to ?? Sunday) });
    }

    private static JsonElement Line(JsonElement period, string name) =>
        period.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("employeeName").GetString() == name);

    private static List<string> Warnings(JsonElement period) =>
        period.GetProperty("warnings").EnumerateArray().Select(w => $"{w.GetProperty("employeeName").GetString()}:{w.GetProperty("code").GetString()}").ToList();

    private Task<HttpResponseMessage> SetBankAsync(Guid employee, string name) =>
        Manager.Put($"{Api}/employees/{employee}/bank-account", new { bankName = "VCB", accountNumber = "0123456789", accountName = name });

    [Fact]
    public async Task Finalizing_freezes_the_pay_and_locks_the_days_of_the_period()
    {
        var (_, employee, assignment) = await WorkedAsync("An");
        await ApproveWeekAsync();

        var period = await FinalizeAsync();

        period.GetProperty("status").GetString().Should().Be("Finalized");
        (Line(period, "An").GetProperty("grossPay").GetDecimal(), Line(period, "An").GetProperty("netPay").GetDecimal()).Should().Be((120_000m, 120_000m));
        period.GetProperty("totalNet").GetDecimal().Should().Be(120_000);
        Warnings(period).Should().Equal("An:NoBankAccount");

        await Manager.Post(Adjustments, new { employeeId = employee, workDate = D(Monday), amount = 50_000, reason = "Thưởng" }).ShouldBe(409, "payroll.period_locked");
        await Manager.Post($"{Api}/attendance/{assignment}/adjust", new { checkOutAt = "2026-11-02T12:00:00+07:00", reason = "Quên" }).ShouldBe(409, "payroll.period_locked");
        await Manager.Post($"{Api}/attendance/{assignment}/absent", new { reason = "Nghỉ" }).ShouldBe(409, "payroll.period_locked");
        await Manager.Post(Adjustments, new { employeeId = employee, workDate = D(Monday.AddDays(7)), amount = 50_000, reason = "Kỳ sau" }).ShouldBe(201);   // the next period is open
    }

    [Fact]
    public async Task Shifts_not_reviewed_days_not_over_and_overlapping_periods_are_refused()
    {
        await WorkedAsync("Bình");
        SetLocal(Monday.AddDays(7), 9);

        await Manager.Post(Periods, new { from = D(Monday), to = D(Sunday) }).ShouldBe(422, "payroll.unreviewed_records");
        await Manager.Post(Periods, new { from = D(Monday), to = D(Monday.AddDays(7)) }).ShouldBe(422, "payroll.period_not_over");
        await Manager.Post(Periods, new { from = D(Sunday), to = D(Monday) }).ShouldBe(400);
        await Admin.Post(Periods, new { from = D(Monday), to = D(Sunday) }).ShouldBe(400);                       // a brand admin names the branch

        await ApproveWeekAsync();
        await FinalizeAsync();
        await Manager.Post(Periods, new { from = D(Monday.AddDays(3)), to = D(Sunday) }).ShouldBe(409, "payroll.period_overlap");
    }

    [Fact]
    public async Task Paying_records_the_transfer_once_and_the_period_is_paid_when_everyone_is()
    {
        var (_, an, _) = await WorkedAsync("An");
        var (_, binh, _) = await WorkedAsync("Bình");
        await SetBankAsync(an, "Nguyen Van An").ShouldBe(200);
        await SetBankAsync(binh, "Tran Thi Binh").ShouldBe(200);
        await ApproveWeekAsync();
        var period = await FinalizeAsync();
        var id = period.Id();
        Warnings(period).Should().BeEmpty();

        var csv = await Manager.GetStringAsync($"{Periods}/{id}/bank-transfer");
        csv.Should().Contain("\"NGUYEN VAN AN\",\"0123456789\",\"VCB\",120000,\"Luong 02/11-08/11/2026\"");
        csv.Should().Contain("TRAN THI BINH");

        await Manager.Post($"{Periods}/{id}/payments", new { employeeIds = new[] { an }, method = "BankTransfer" }).ShouldBe(400);   // a transfer needs its reference
        var paid = await Manager.PostOk($"{Periods}/{id}/payments", new { employeeIds = new[] { an }, method = "BankTransfer", reference = "FT26316000001" });

        paid.GetProperty("status").GetString().Should().Be("Finalized");                                   // Bình's transfer failed: still to pay
        (paid.GetProperty("paid").GetInt32(), paid.GetProperty("totalPaid").GetDecimal()).Should().Be((1, 120_000m));
        Line(paid, "An").GetProperty("payoutReference").GetString().Should().Be("FT26316000001");
        (await Manager.GetStringAsync($"{Periods}/{id}/bank-transfer")).Should().NotContain("NGUYEN VAN AN");
        await Manager.Post($"{Periods}/{id}/payments", new { employeeIds = new[] { an, binh }, method = "Cash" }).ShouldBe(409, "payroll.already_paid");
        Line(await Manager.GetFromJsonAsync<JsonElement>($"{Periods}/{id}"), "Bình").GetProperty("isPaid").GetBoolean().Should().BeFalse();   // all or nothing

        var all = await Manager.PostOk($"{Periods}/{id}/payments", new { employeeIds = new[] { binh }, method = "Cash" });

        all.GetProperty("status").GetString().Should().Be("Paid");
        await Manager.Post($"{Periods}/{id}/reopen", new { reason = "Sửa" }).ShouldBe(409, "payroll.period_paid");
    }

    [Fact]
    public async Task A_reopened_period_unlocks_its_days_and_can_be_finalized_again_with_the_correction()
    {
        var (_, employee, _) = await WorkedAsync("An");
        await ApproveWeekAsync();
        var first = await FinalizeAsync();

        await Manager.Post($"{Periods}/{first.Id()}/reopen", new { }).ShouldBe(400);
        var reopened = await Manager.PostOk($"{Periods}/{first.Id()}/reopen", new { reason = "Thiếu thưởng" });
        reopened.GetProperty("status").GetString().Should().Be("Reopened");
        await Manager.Post($"{Periods}/{first.Id()}/payments", new { employeeIds = new[] { employee }, method = "Cash" }).ShouldBe(409, "payroll.period_reopened");

        await Manager.PostOk(Adjustments, new { employeeId = employee, workDate = D(Monday), amount = 30_000, reason = "Thưởng" });
        var second = await FinalizeAsync();

        Line(second, "An").GetProperty("netPay").GetDecimal().Should().Be(150_000);
        (await Manager.GetFromJsonAsync<JsonElement>(Periods)).GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Deductions_larger_than_the_pay_leave_nothing_to_pay_and_a_warning()
    {
        var (_, employee, _) = await WorkedAsync("An");
        SetLocal(Monday.AddDays(1), 8);
        await Manager.PostOk(Adjustments, new { employeeId = employee, workDate = D(Monday), amount = -500_000, reason = "Làm hỏng máy" });
        await ApproveWeekAsync();

        var period = await FinalizeAsync();

        (Line(period, "An").GetProperty("grossPay").GetDecimal(), Line(period, "An").GetProperty("netPay").GetDecimal()).Should().Be((-380_000m, 0m));
        Warnings(period).Should().Equal("An:DeductionsExceedPay");
        period.GetProperty("status").GetString().Should().Be("Paid");                                         // nothing is owed to anyone
        await Manager.Post($"{Periods}/{period.Id()}/payments", new { employeeIds = new[] { employee }, method = "Cash" }).ShouldBe(422, "payroll.nothing_to_pay");
    }

    [Fact]
    public async Task Each_employee_reads_only_their_own_payslips_and_sets_their_own_salary_account()
    {
        var (anUser, an, _) = await WorkedAsync("An");
        var (binhUser, _, _) = await WorkedAsync("Bình");
        await Staff(anUser).Put($"{Api}/employees/me/bank-account", new { bankName = "MB", accountNumber = "999988887777" }).ShouldBe(400);   // all three parts
        await Staff(anUser).Put($"{Api}/employees/me/bank-account", new { bankName = "MB", accountNumber = "999988887777", accountName = "Nguyen Van An" }).ShouldBe(200);
        await ApproveWeekAsync();
        var period = await FinalizeAsync();
        await Manager.PostOk($"{Periods}/{period.Id()}/payments", new { employeeIds = new[] { an }, method = "BankTransfer", reference = "FT1" });

        var mine = await Staff(anUser).GetFromJsonAsync<JsonElement>($"{Api}/payroll/payslips/mine");

        mine.GetArrayLength().Should().Be(1);
        var slip = mine[0];
        (slip.GetProperty("netPay").GetDecimal(), slip.GetProperty("isPaid").GetBoolean(), slip.GetProperty("bankAccountHint").GetString()).Should().Be((120_000m, true, "••••7777"));
        (await Staff(binhUser).GetFromJsonAsync<JsonElement>($"{Api}/payroll/payslips/mine"))[0].GetProperty("isPaid").GetBoolean().Should().BeFalse();
        (await Staff(anUser).GetAsync($"{Periods}/{period.Id()}")).StatusCode.Should().Be(System.Net.HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_manager_of_another_branch_cannot_see_or_pay_the_period()
    {
        var (_, employee, _) = await WorkedAsync("An");
        await ApproveWeekAsync();
        var period = await FinalizeAsync();

        (await Manager2.GetAsync($"{Periods}/{period.Id()}")).StatusCode.Should().Be(System.Net.HttpStatusCode.Forbidden);
        await Manager2.Post($"{Periods}/{period.Id()}/payments", new { employeeIds = new[] { employee }, method = "Cash" }).ShouldBe(403);
        (await Admin.GetFromJsonAsync<JsonElement>($"{Periods}?branchId={B1}")).GetArrayLength().Should().Be(1);
    }
}
