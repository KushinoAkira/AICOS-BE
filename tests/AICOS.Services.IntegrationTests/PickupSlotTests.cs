using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;
using AICOS.Ordering.Api;
using AICOS.Ordering.Api.Persistence;

namespace AICOS.Services.IntegrationTests;

/// <summary>UC-21 pickup slot capacity and cut-off, per brand with an optional override per branch.</summary>
public sealed class PickupSlotTests : IDisposable
{
    private const string Url = "/api/v1/orders/pickup-slots";

    private readonly ServiceFactory<OrderingService, OrderingDbContext> _f = new();
    private readonly Guid _tenant = ServiceFactory<OrderingService, OrderingDbContext>.DefaultTenantId;
    private readonly Guid _otherTenant = Guid.NewGuid();
    private readonly Guid _b1;
    private readonly Guid _b2;
    private readonly Guid _foreign;
    private readonly HttpClient _admin;
    private readonly HttpClient _otherAdmin;
    private readonly HttpClient _manager1;

    public PickupSlotTests()
    {
        _b1 = _f.Branches.Add(_tenant, name: "Quận 1");
        _b2 = _f.Branches.Add(_tenant, name: "Quận 3");
        _foreign = _f.Branches.Add(_otherTenant, name: "Foreign");
        _admin = _f.As(AicosRoles.BrandAdmin);
        _otherAdmin = _f.As(AicosRoles.BrandAdmin, tenantId: _otherTenant);
        _manager1 = _f.As(AicosRoles.BranchManager, _b1);
    }

    public void Dispose() => _f.Dispose();

    private static object Body(Guid? branchId = null, int slot = 15, int capacity = 8, int cutoff = 20, int advance = 2) =>
        new { branchId, slotMinutes = slot, capacityPerSlot = capacity, cutoffMinutes = cutoff, advanceDays = advance };

    private async Task<JsonElement> EffectiveAsync(HttpClient client, Guid? branchId = null) =>
        await client.GetFromJsonAsync<JsonElement>(branchId is null ? $"{Url}/effective" : $"{Url}/effective?branchId={branchId}");

    [Fact]
    public async Task Without_configuration_the_platform_defaults_apply()
    {
        var e = await EffectiveAsync(_f.As(AicosRoles.Cashier, _b1));

        e.GetProperty("source").GetString().Should().Be("Default");
        e.GetProperty("slotMinutes").GetInt32().Should().Be(15);
        e.GetProperty("capacityPerSlot").GetInt32().Should().Be(10);
        e.GetProperty("cutoffMinutes").GetInt32().Should().Be(15);
        e.GetProperty("advanceDays").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task The_brand_default_applies_to_every_branch_until_a_branch_overrides_it()
    {
        (await _admin.Put(Url, Body())).StatusCode.Should().Be(System.Net.HttpStatusCode.Created);
        (await _admin.Put(Url, Body(capacity: 12))).StatusCode.Should().Be(System.Net.HttpStatusCode.OK);   // same row updated

        foreach (var branch in new[] { _b1, _b2 })
        {
            var e = await EffectiveAsync(_f.As(AicosRoles.Cashier, branch));
            e.GetProperty("source").GetString().Should().Be("Brand");
            e.GetProperty("capacityPerSlot").GetInt32().Should().Be(12);
        }

        await _admin.Put(Url, Body(_b2, capacity: 3, cutoff: 45)).ShouldBe(201);

        var atB2 = await EffectiveAsync(_f.As(AicosRoles.Customer), _b2);
        atB2.GetProperty("source").GetString().Should().Be("Branch");
        atB2.GetProperty("capacityPerSlot").GetInt32().Should().Be(3);
        atB2.GetProperty("cutoffMinutes").GetInt32().Should().Be(45);
        (await EffectiveAsync(_f.As(AicosRoles.Customer), _b1)).GetProperty("source").GetString().Should().Be("Brand");
        (await _admin.GetFromJsonAsync<JsonElement>(Url)).GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task A_branch_manager_sets_only_their_own_branch_override()
    {
        await _manager1.Put(Url, Body(_b1, capacity: 5)).ShouldBe(201);
        (await EffectiveAsync(_manager1)).GetProperty("capacityPerSlot").GetInt32().Should().Be(5);

        await _manager1.Put(Url, Body(_b2)).ShouldBe(403, "branch.forbidden");
        await _manager1.Put(Url, Body()).ShouldBe(403);                                                  // brand default is for the brand admin

        await _admin.Put(Url, Body()).ShouldBe(201);
        await _admin.Put(Url, Body(_b2)).ShouldBe(201);
        var seen = await _manager1.GetFromJsonAsync<JsonElement>(Url);
        seen.GetArrayLength().Should().Be(2);                                                             // brand default + own branch, not branch 2

        var b2Row = (await _admin.GetFromJsonAsync<JsonElement>(Url)).EnumerateArray().First(s => s.TryGetProperty("branchId", out var b) && b.ValueKind == JsonValueKind.String && b.GetGuid() == _b2);
        await _manager1.DeleteAsync($"{Url}/{b2Row.Id()}").ShouldBe(403, "branch.forbidden");
    }

    [Fact]
    public async Task Removing_an_override_returns_the_branch_to_the_brand_default()
    {
        await _admin.Put(Url, Body(capacity: 20)).ShouldBe(201);
        var row = await _admin.PostPutOk(Url, Body(_b1, capacity: 2));
        (await EffectiveAsync(_manager1)).GetProperty("capacityPerSlot").GetInt32().Should().Be(2);

        await _manager1.DeleteAsync($"{Url}/{row.Id()}").ShouldBe(204);

        (await EffectiveAsync(_manager1)).GetProperty("capacityPerSlot").GetInt32().Should().Be(20);
        await _admin.DeleteAsync($"{Url}/{row.Id()}").ShouldBe(404);
    }

    [Fact]
    public async Task Input_is_validated()
    {
        await _admin.Put(Url, new { }).ShouldBe(400);
        await _admin.Put(Url, Body(slot: 4)).ShouldBe(400);
        await _admin.Put(Url, Body(slot: 121)).ShouldBe(400);
        await _admin.Put(Url, Body(capacity: 0)).ShouldBe(400);
        await _admin.Put(Url, Body(capacity: 501)).ShouldBe(400);
        await _admin.Put(Url, Body(cutoff: -1)).ShouldBe(400);
        await _admin.Put(Url, Body(cutoff: 241)).ShouldBe(400);
        await _admin.Put(Url, Body(advance: 15)).ShouldBe(400);
        await _admin.Put(Url, Body(cutoff: 0, advance: 0)).ShouldBe(201);                                // the limits themselves are fine
    }

    [Fact]
    public async Task Settings_are_isolated_per_brand_and_branches_of_other_brands_are_refused()
    {
        await _admin.Put(Url, Body(capacity: 7)).ShouldBe(201);

        await _admin.Put(Url, Body(_foreign)).ShouldBe(400);
        await _admin.Put(Url, Body(Guid.NewGuid())).ShouldBe(400);
        (await _otherAdmin.GetFromJsonAsync<JsonElement>(Url)).GetArrayLength().Should().Be(0);
        (await EffectiveAsync(_otherAdmin, _foreign)).GetProperty("source").GetString().Should().Be("Default");
        var mine = (await _admin.GetFromJsonAsync<JsonElement>(Url))[0];
        await _otherAdmin.DeleteAsync($"{Url}/{mine.Id()}").ShouldBe(404);
    }

    [Fact]
    public async Task Staff_and_customers_cannot_change_or_list_settings_but_can_read_the_effective_rules()
    {
        foreach (var client in new[] { _f.As(AicosRoles.Cashier, _b1), _f.As(AicosRoles.Barista, _b1), _f.As(AicosRoles.Customer), _f.As(AicosRoles.SuperAdmin) })
        {
            await client.Put(Url, Body(_b1)).ShouldBe(403);
            await client.GetAsync(Url).ShouldBe(403);
        }

        await _f.As(AicosRoles.Customer).GetAsync($"{Url}/effective").ShouldBe(200);
        await _f.CreateClient().GetAsync($"{Url}/effective").ShouldBe(401);
        await _f.As(AicosRoles.SuperAdmin).GetAsync($"{Url}/effective").ShouldBe(403, "tenant.required");
    }

    [Fact]
    public async Task Changes_are_sent_to_the_audit_history_with_before_and_after()
    {
        var row = await _admin.PostPutOk(Url, Body(_b1, capacity: 4));
        await _admin.Put(Url, Body(_b1, capacity: 6)).ShouldBe(200);
        await _admin.DeleteAsync($"{Url}/{row.Id()}").ShouldBe(204);

        var events = _f.Events.Published.OfType<AuditRecordedEvent>().ToList();

        events.Select(e => e.Action).Should().Equal("pickup_slot_setting.created", "pickup_slot_setting.updated", "pickup_slot_setting.deleted");
        events.Should().OnlyContain(e => e.TenantId == _tenant && e.EntityType == "PickupSlotSetting");
        events[1].Before.Should().Contain("\"capacityPerSlot\":4");
        events[1].After.Should().Contain("\"capacityPerSlot\":6");
    }
}
