using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.WorkforceScheduling.Api;
using AICOS.WorkforceScheduling.Api.Persistence;

namespace AICOS.Services.IntegrationTests;

public sealed class WorkforceTests : IDisposable
{
    private static readonly TimeZoneInfo Vietnam = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");

    private readonly ServiceFactory<WorkforceSchedulingService, WorkforceSchedulingDbContext> _f = new();
    private readonly Guid _branch = Guid.NewGuid();
    private readonly HttpClient _manager;

    public WorkforceTests() => _manager = _f.As(AicosRoles.Manager, _branch);

    public void Dispose() => _f.Dispose();

    private DateOnly Tomorrow => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(_f.Clock.Now.UtcDateTime, Vietnam)).AddDays(1);

    [Fact]
    public async Task Employee_rules()
    {
        var userId = Guid.NewGuid();
        await _manager.Post("/api/v1/workforce/employees", new { userId, fullName = "An", paymentType = "Hourly" }).ShouldBe(400); // missing rate
        await _manager.PostOk("/api/v1/workforce/employees", new { userId, fullName = "An", paymentType = "Hourly", hourlyRate = 25000 });
        await _manager.Post("/api/v1/workforce/employees", new { userId, fullName = "An 2", paymentType = "Fixed", fixedSalary = 8000000 }).ShouldBe(409, "employee.duplicate_user");

        var me = await _f.As(AicosRoles.Cashier, _branch, userId).GetFromJsonAsync<JsonElement>("/api/v1/workforce/employees/me");
        me.GetProperty("fullName").GetString().Should().Be("An");
    }

    [Fact]
    public async Task Shift_template_validation()
    {
        await _manager.Post("/api/v1/workforce/work-shifts", new { name = "Đêm", startTime = "22:00", endTime = "06:00" }).ShouldBe(400);
        await _manager.Post("/api/v1/workforce/work-shifts", new { name = "Ngắn", startTime = "08:00", endTime = "08:30" }).ShouldBe(400);
        var shift = await _manager.PostOk("/api/v1/workforce/work-shifts", new { name = "Sáng", startTime = "06:00", endTime = "12:00" });
        shift.GetProperty("hours").GetDouble().Should().Be(6);
        await _manager.Post("/api/v1/workforce/work-shifts", new { name = "sáng", startTime = "07:00", endTime = "11:00" }).ShouldBe(409, "shift.duplicate_name");
    }

    [Fact]
    public async Task Overlapping_shift_and_past_date_are_rejected_BR22()
    {
        var (employee, _) = await Employee("An");
        var morning = await Shift("Sáng", "06:00", "12:00");
        var mid = await Shift("Trưa", "10:00", "14:00");
        var afternoon = await Shift("Chiều", "12:00", "18:00");

        await Assign(employee, morning, Tomorrow);
        await _manager.Post("/api/v1/workforce/shift-assignments", new { employeeId = employee, workShiftId = mid, workDate = Tomorrow }).ShouldBe(409, "shift.overlap");
        await Assign(employee, afternoon, Tomorrow);
        await _manager.Post("/api/v1/workforce/shift-assignments", new { employeeId = employee, workShiftId = morning, workDate = Tomorrow.AddDays(-3) }).ShouldBe(400);
    }

    [Fact]
    public async Task Weekly_hours_cap_is_enforced()
    {
        var (employee, _) = await Employee("An");
        var shift = await Shift("Dài", "06:00", "18:00");
        var monday = NextMonday();
        for (var i = 0; i < 4; i++)
        {
            await Assign(employee, shift, monday.AddDays(i)); // 4 x 12h = 48h
        }

        await _manager.Post("/api/v1/workforce/shift-assignments", new { employeeId = employee, workShiftId = shift, workDate = monday.AddDays(4) })
            .ShouldBe(422, "shift.weekly_hours_exceeded");
    }

    [Fact]
    public async Task Employee_confirms_own_shift_only_and_can_decline()
    {
        var (employee, user) = await Employee("An");
        var (_, otherUser) = await Employee("Bình");
        var shift = await Shift("Sáng", "06:00", "12:00");
        var assignment = await Assign(employee, shift, Tomorrow);

        await _f.As(AicosRoles.Cashier, _branch, otherUser).Post($"/api/v1/workforce/shift-assignments/{assignment}/confirm", new { }).ShouldBe(404);
        await _f.As(AicosRoles.Cashier, _branch, user).Post($"/api/v1/workforce/shift-assignments/{assignment}/confirm", new { }).ShouldBe(200);
        await _f.As(AicosRoles.Cashier, _branch, user).Post($"/api/v1/workforce/shift-assignments/{assignment}/decline", new { reason = "" }).ShouldBe(400);
        await _f.As(AicosRoles.Cashier, _branch, user).Post($"/api/v1/workforce/shift-assignments/{assignment}/decline", new { reason = "Ốm" }).ShouldBe(200);
    }

    [Fact]
    public async Task Swap_moves_the_shift_only_after_manager_approval_BR23()
    {
        var (an, anUser) = await Employee("An");
        var (binh, binhUser) = await Employee("Bình");
        var shift = await Shift("Sáng", "06:00", "12:00");
        var assignment = await Assign(an, shift, Tomorrow);
        var anClient = _f.As(AicosRoles.Cashier, _branch, anUser);

        var swap = await anClient.PostOk("/api/v1/workforce/shift-swaps", new { assignmentId = assignment, targetEmployeeId = binh, reason = "Bận việc gia đình" });
        await anClient.Post("/api/v1/workforce/shift-swaps", new { assignmentId = assignment, targetEmployeeId = binh, reason = "Lần 2" }).ShouldBe(409, "swap.already_pending");

        var before = await _manager.GetFromJsonAsync<JsonElement>($"/api/v1/workforce/shift-assignments/{assignment}");
        before.GetProperty("employeeId").GetGuid().Should().Be(an);

        await anClient.Post($"/api/v1/workforce/shift-swaps/{swap.Id()}/approve", new { }).ShouldBe(403);
        await _manager.Post($"/api/v1/workforce/shift-swaps/{swap.Id()}/approve", new { note = "OK" }).ShouldBe(200);

        var mine = await _f.As(AicosRoles.Cashier, _branch, binhUser).GetFromJsonAsync<JsonElement>($"/api/v1/workforce/shift-assignments/mine?from={Tomorrow:yyyy-MM-dd}");
        mine.GetArrayLength().Should().Be(1);
        mine[0].GetProperty("status").GetString().Should().Be("Assigned");
    }

    [Fact]
    public async Task Swap_to_a_colleague_who_is_busy_is_409()
    {
        var (an, anUser) = await Employee("An");
        var (binh, _) = await Employee("Bình");
        var morning = await Shift("Sáng", "06:00", "12:00");
        var assignment = await Assign(an, morning, Tomorrow);
        await Assign(binh, morning, Tomorrow);

        await _f.As(AicosRoles.Cashier, _branch, anUser).Post("/api/v1/workforce/shift-swaps", new { assignmentId = assignment, targetEmployeeId = binh, reason = "x" })
            .ShouldBe(409, "shift.overlap");
    }

    [Fact]
    public async Task Check_in_window_and_double_tap_rules_BR24_BR46()
    {
        var (employee, user) = await Employee("An");
        var day = Tomorrow;
        var shift = await Shift("Sáng", "08:00", "12:00");
        var assignment = await Assign(employee, shift, day);
        var client = _f.As(AicosRoles.Cashier, _branch, user);
        var body = new { assignmentId = assignment };

        SetLocalTime(day, new TimeOnly(7, 30)); // 30 minutes early
        await client.Post("/api/v1/workforce/attendance/check-in", body).ShouldBe(422, "BR-24");

        SetLocalTime(day, new TimeOnly(7, 50));
        await client.Post("/api/v1/workforce/attendance/check-in", body).ShouldBe(200);
        await client.Post("/api/v1/workforce/attendance/check-in", body).ShouldBe(409, "attendance.already_checked_in");

        SetLocalTime(day, new TimeOnly(7, 53));
        await client.Post("/api/v1/workforce/attendance/check-out", body).ShouldBe(422, "BR-46");

        SetLocalTime(day, new TimeOnly(12, 5));
        var done = await client.PostOk("/api/v1/workforce/attendance/check-out", body);
        done.GetProperty("checkOutAtUtc").ValueKind.Should().NotBe(JsonValueKind.Null);

        await _f.As(AicosRoles.Cashier, _branch).Post("/api/v1/workforce/attendance/check-in", body).ShouldBe(404); // no employee profile
    }

    private async Task<(Guid EmployeeId, Guid UserId)> Employee(string name)
    {
        var userId = Guid.NewGuid();
        var employee = await _manager.PostOk("/api/v1/workforce/employees", new { userId, fullName = name, position = "Barista", paymentType = "Hourly", hourlyRate = 25000 });
        return (employee.Id(), userId);
    }

    private async Task<Guid> Shift(string name, string start, string end) =>
        (await _manager.PostOk("/api/v1/workforce/work-shifts", new { name, startTime = start, endTime = end })).Id();

    private async Task<Guid> Assign(Guid employee, Guid shift, DateOnly date) =>
        (await _manager.PostOk("/api/v1/workforce/shift-assignments", new { employeeId = employee, workShiftId = shift, workDate = date })).Id();

    private DateOnly NextMonday()
    {
        var day = Tomorrow;
        while (day.DayOfWeek != DayOfWeek.Monday)
        {
            day = day.AddDays(1);
        }

        return day;
    }

    private void SetLocalTime(DateOnly date, TimeOnly time) =>
        _f.Clock.Now = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(time), Vietnam), TimeSpan.Zero);
}
