using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using AICOS.WorkforceScheduling.Api;
using AICOS.WorkforceScheduling.Api.Persistence;

namespace AICOS.Services.IntegrationTests;

/// <summary>
/// Shared setup for the Workforce tests: two branches of one brand, a second brand, and helpers to create employees, shifts and
/// assignments. The clock is fixed to a Monday in shop time (UTC+7), so shift windows can be crossed on purpose.
/// </summary>
public abstract class WorkforceTestBase : IDisposable
{
    protected const string Api = "/api/v1/workforce";

    /// <summary>A Monday, so that weekly-hour rules start from a known day.</summary>
    protected static readonly DateOnly Monday = new(2026, 11, 2);

    protected readonly FakeOrderExceptions OrderExceptions = new();
    protected readonly ServiceFactory<WorkforceSchedulingService, WorkforceSchedulingDbContext> F;
    protected readonly Guid Tenant = ServiceFactory<WorkforceSchedulingService, WorkforceSchedulingDbContext>.DefaultTenantId;
    protected readonly Guid OtherTenant = Guid.NewGuid();
    protected readonly Guid B1;
    protected readonly Guid B2;
    protected readonly Guid OtherBranch;
    protected readonly HttpClient Admin;
    protected readonly HttpClient OtherAdmin;
    protected readonly HttpClient Manager;
    protected readonly HttpClient Manager2;

    protected WorkforceTestBase()
    {
        F = new ServiceFactory<WorkforceSchedulingService, WorkforceSchedulingDbContext>(services =>
        {
            services.RemoveAll<AICOS.WorkforceScheduling.Api.Infrastructure.IOrderExceptionDirectory>();
            services.AddSingleton<AICOS.WorkforceScheduling.Api.Infrastructure.IOrderExceptionDirectory>(OrderExceptions);
        });
        B1 = F.Branches.Add(Tenant, name: "Quận 1");
        B2 = F.Branches.Add(Tenant, name: "Quận 3");
        OtherBranch = F.Branches.Add(OtherTenant, name: "Foreign");
        Admin = F.As(AicosRoles.BrandAdmin);
        OtherAdmin = F.As(AicosRoles.BrandAdmin, tenantId: OtherTenant);
        Manager = F.As(AicosRoles.BranchManager, B1);
        Manager2 = F.As(AicosRoles.BranchManager, B2);
        SetLocal(Monday, 6, 0);
    }

    public void Dispose() => F.Dispose();

    /// <summary>Moves the clock to a wall-clock time of the shop (UTC+7).</summary>
    protected void SetLocal(DateOnly date, int hour, int minute = 0) =>
        F.Clock.Now = new DateTimeOffset(date.ToDateTime(new TimeOnly(hour, minute)), TimeSpan.FromHours(7)).ToUniversalTime();

    protected static string D(DateOnly date) => date.ToString("yyyy-MM-dd");

    protected Guid NewAccount(string role, Guid? branch, string name, Guid? tenant = null, bool active = true) =>
        F.Users.Add(role, branch, name, tenant, active);

    /// <summary>A Barista / Cashier account and its employee profile, created by the branch manager.</summary>
    protected async Task<(Guid UserId, Guid EmployeeId)> NewEmployeeAsync(
        string name, string role = AicosRoles.Barista, string contract = "PartTime", Guid? branch = null)
    {
        var branchId = branch ?? B1;
        var userId = NewAccount(role, branchId, name);
        var client = branchId == B1 ? Manager : Manager2;
        var employee = await client.PostOk($"{Api}/employees", new { userId, contractType = contract });
        return (userId, employee.Id());
    }

    protected async Task<Guid> NewShiftAsync(string name, string start, string end, Guid? branch = null)
    {
        var client = (branch ?? B1) == B1 ? Manager : Manager2;
        return (await client.PostOk($"{Api}/work-shifts", new { name, startTime = start, endTime = end })).Id();
    }

    protected async Task<Guid> AssignAsync(Guid employeeId, Guid shiftId, DateOnly date, HttpClient? client = null) =>
        (await (client ?? Manager).PostOk($"{Api}/shift-assignments", new { employeeId, workShiftId = shiftId, workDate = D(date) })).Id();

    protected HttpClient Staff(Guid userId, string role = AicosRoles.Barista, Guid? branch = null) => F.As(role, branch ?? B1, userId);

    protected async Task<JsonElement> GetAssignmentAsync(Guid id) => await Manager.GetFromJsonAsync<JsonElement>($"{Api}/shift-assignments/{id}");
}

/// <summary>Stands in for the Order service: the orders cancelled after payment that a test wants a cash closing to see.</summary>
public sealed class FakeOrderExceptions : AICOS.WorkforceScheduling.Api.Infrastructure.IOrderExceptionDirectory
{
    public List<AICOS.WorkforceScheduling.Api.Infrastructure.OrderException> Items { get; } = [];

    public (Guid Branch, DateOnly From, DateOnly To)? LastQuery { get; private set; }

    public void Add(string code, decimal amount, string refundStatus, DateTime cancelledAtUtc, decimal refunded = 0) =>
        Items.Add(new AICOS.WorkforceScheduling.Api.Infrastructure.OrderException(Guid.NewGuid(), code, amount, refunded, "Cancelled", refundStatus, "Cash", cancelledAtUtc));

    public Task<IReadOnlyList<AICOS.WorkforceScheduling.Api.Infrastructure.OrderException>> ListAsync(Guid branchId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        LastQuery = (branchId, from, to);
        return Task.FromResult<IReadOnlyList<AICOS.WorkforceScheduling.Api.Infrastructure.OrderException>>(Items.ToList());
    }
}
