using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;

namespace AICOS.Services.IntegrationTests;

/// <summary>The brand admin pays the branch managers a monthly salary; managers pay their staff; the brand admin only reads the staff payroll.</summary>
public sealed class WorkforceManagerPayrollTests : WorkforceTestBase
{
    private const string Managers = $"{Api}/payroll/managers";
    private const string ManagerPeriods = $"{Api}/payroll/manager-periods";
    private const string Periods = $"{Api}/payroll/periods";

    private async Task<JsonElement> SetSalaryAsync(Guid userId, decimal amount)
    {
        var response = await Admin.Put($"{Managers}/{userId}/salary", new { monthlySalary = amount });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static JsonElement Line(JsonElement period, string name) =>
        period.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("employeeName").GetString() == name);

    [Fact]
    public async Task The_brand_admin_sets_salaries_finalizes_a_month_with_bonuses_and_pays_the_managers()
    {
        var lan = NewAccount(AicosRoles.BranchManager, B1, "Lan");
        var minh = NewAccount(AicosRoles.BranchManager, B2, "Minh");
        var first = await SetSalaryAsync(lan, 12_000_000);
        await SetSalaryAsync(minh, 10_000_000);
        var changed = await SetSalaryAsync(lan, 13_000_000);                                                        // same profile, new amount
        changed.GetProperty("employeeId").GetGuid().Should().Be(first.GetProperty("employeeId").GetGuid());
        (changed.GetProperty("branchId").GetGuid(), changed.GetProperty("monthlySalary").GetDecimal()).Should().Be((B1, 13_000_000m));
        (await Admin.GetFromJsonAsync<JsonElement>(Managers)).GetArrayLength().Should().Be(2);
        var minhId = (await Admin.GetFromJsonAsync<JsonElement>(Managers)).EnumerateArray().Single(m => m.GetProperty("fullName").GetString() == "Minh").GetProperty("employeeId").GetGuid();
        await F.As(AicosRoles.BranchManager, B1, lan).Put($"{Api}/employees/me/bank-account", new { bankName = "VCB", accountNumber = "0123456789", accountName = "NGUYEN THI LAN" }).ShouldBe(200);

        SetLocal(new DateOnly(2026, 12, 1), 9);
        var period = await Admin.PostOk(ManagerPeriods, new { month = "2026-11", adjustments = new[] { new { employeeId = minhId, amount = -500_000m } } });

        (period.GetProperty("kind").GetString(), period.GetProperty("from").GetString(), period.GetProperty("to").GetString()).Should().Be(("Managers", "2026-11-01", "2026-11-30"));
        (Line(period, "Lan").GetProperty("netPay").GetDecimal(), Line(period, "Minh").GetProperty("netPay").GetDecimal()).Should().Be((13_000_000m, 9_500_000m));
        period.GetProperty("totalNet").GetDecimal().Should().Be(22_500_000m);
        await Admin.Post(ManagerPeriods, new { month = "2026-11" }).ShouldBe(409, "payroll.period_overlap");

        var lanEmployee = first.GetProperty("employeeId").GetGuid();
        var paid = await Admin.PostOk($"{Periods}/{period.Id()}/payments", new { employeeIds = new[] { lanEmployee }, method = "BankTransfer", reference = "FT26120100001" });
        (paid.GetProperty("paid").GetInt32(), paid.GetProperty("status").GetString()).Should().Be((1, "Finalized"));

        var slips = await F.As(AicosRoles.BranchManager, B1, lan).GetFromJsonAsync<JsonElement>($"{Api}/payroll/payslips/mine");
        (slips[0].GetProperty("kind").GetString(), slips[0].GetProperty("netPay").GetDecimal(), slips[0].GetProperty("isPaid").GetBoolean()).Should().Be(("Managers", 13_000_000m, true));
        (await Admin.GetFromJsonAsync<JsonElement>(ManagerPeriods)).GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Managers_cannot_see_or_pay_the_managers_payroll_and_the_brand_admin_cannot_pay_staff()
    {
        var lan = NewAccount(AicosRoles.BranchManager, B1, "Lan");
        await SetSalaryAsync(lan, 12_000_000);
        SetLocal(new DateOnly(2026, 12, 1), 9);
        var period = await Admin.PostOk(ManagerPeriods, new { month = "2026-11" });

        await Manager.GetAsync(ManagerPeriods).ShouldBe(403);
        await Manager.Put($"{Managers}/{lan}/salary", new { monthlySalary = 99_000_000 }).ShouldBe(403);
        await Manager.GetAsync($"{Periods}/{period.Id()}").ShouldBe(404);
        await Manager.Post($"{Periods}/{period.Id()}/reopen", new { reason = "Sửa" }).ShouldBe(404);
        (await Manager.GetFromJsonAsync<JsonElement>(Periods)).GetArrayLength().Should().Be(0);               // the staff list holds no managers period
        (await Manager.GetFromJsonAsync<JsonElement>($"{Api}/employees")).GetProperty("items").GetArrayLength().Should().Be(0);   // nor the staff list a manager

        await Admin.Post(Periods, new { branchId = B1, from = "2026-11-01", to = "2026-11-30" }).ShouldBe(403, "payroll.manager_pays_staff");
        var lanEmployee = (await Admin.GetFromJsonAsync<JsonElement>(Managers))[0].GetProperty("employeeId").GetGuid();
        await Admin.Post($"{Api}/payroll/adjustments", new { employeeId = lanEmployee, workDate = "2026-11-03", amount = 100_000, reason = "Thưởng" }).ShouldBe(422, "payroll.manager_adjustment");
    }

    [Fact]
    public async Task Bad_salaries_months_not_over_and_staff_accounts_are_refused()
    {
        var lan = NewAccount(AicosRoles.BranchManager, B1, "Lan");
        var cashier = NewAccount(AicosRoles.Cashier, B1, "Thu");

        await Admin.Put($"{Managers}/{lan}/salary", new { monthlySalary = -1 }).ShouldBe(400);
        await Admin.Put($"{Managers}/{lan}/salary", new { monthlySalary = 1.5 }).ShouldBe(400);
        await Admin.Put($"{Managers}/{cashier}/salary", new { monthlySalary = 5_000_000 }).ShouldBe(400);
        SetLocal(new DateOnly(2026, 11, 20), 9);
        await Admin.Post(ManagerPeriods, new { month = "2026-10" }).ShouldBe(422, "payroll.no_managers");
        await SetSalaryAsync(lan, 12_000_000);
        await Admin.Post(ManagerPeriods, new { month = "2026-11" }).ShouldBe(422, "payroll.period_not_over");
        await Admin.Post(ManagerPeriods, new { month = "11/2026" }).ShouldBe(400);
        await Admin.Post(ManagerPeriods, new { month = "2026-10", adjustments = new[] { new { employeeId = Guid.NewGuid(), amount = 1000m } } }).ShouldBe(400);
    }
}
