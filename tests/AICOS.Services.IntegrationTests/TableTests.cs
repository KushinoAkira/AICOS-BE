using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.TableReservation.Api;
using AICOS.TableReservation.Api.Features.DiningTables;
using AICOS.TableReservation.Api.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AICOS.Services.IntegrationTests;

/// <summary>Stands in for the Identity service: what the plan of the brand allows.</summary>
public sealed class FakePlanLimits : IPlanLimitsClient
{
    public TablePlanLimits Current { get; set; } = new(true, null);

    public Task<TablePlanLimits> GetAsync(CancellationToken ct) => Task.FromResult(Current);
}

/// <summary>UC-25 floor plan (areas and tables), UC-26 table QR codes and BR-18 QR tokens, with tenant and branch isolation.</summary>
public sealed class TableTests : IDisposable
{
    private const string Areas = "/api/v1/tables/areas";
    private const string Tables = "/api/v1/tables/dining-tables";
    private const string PublicQr = "/api/v1/tables/public/qr";

    private const string Batch = "/api/v1/tables/dining-tables/batch";

    private readonly FakePlanLimits _limits = new();
    private readonly ServiceFactory<TableReservationService, TableReservationDbContext> _f;
    private readonly Guid _tenant = ServiceFactory<TableReservationService, TableReservationDbContext>.DefaultTenantId;
    private readonly Guid _otherTenant = Guid.NewGuid();
    private readonly Guid _b1;
    private readonly Guid _b2;
    private readonly Guid _otherBranch;
    private readonly HttpClient _admin;
    private readonly HttpClient _otherAdmin;
    private readonly HttpClient _manager;
    private readonly HttpClient _cashier;

    public TableTests()
    {
        _f = new(s =>
        {
            s.RemoveAll<IPlanLimitsClient>();
            s.AddSingleton<IPlanLimitsClient>(_limits);
        });
        _b1 = _f.Branches.Add(_tenant, name: "Quận 1");
        _b2 = _f.Branches.Add(_tenant, name: "Quận 3");
        _otherBranch = _f.Branches.Add(_otherTenant, name: "Foreign");
        _admin = _f.As(AicosRoles.BrandAdmin);
        _otherAdmin = _f.As(AicosRoles.BrandAdmin, tenantId: _otherTenant);
        _manager = _f.As(AicosRoles.BranchManager, _b1);
        _cashier = _f.As(AicosRoles.Cashier, _b1);
    }

    public void Dispose() => _f.Dispose();

    // ------------------------------------------------------------------ many tables at once
    private static object[] Numbered(Guid area, string prefix, int count) =>
        Enumerable.Range(1, count).Select(i => (object)new { areaId = area, tableCode = $"{prefix}{i:00}", capacity = 4 }).ToArray();

    [Fact]
    public async Task Generating_creates_every_table_with_its_own_qr_code_in_one_call()
    {
        var area = await _manager.PostOk(Areas, new { name = "Tầng 1" });

        var result = await _manager.PostOk(Batch, new { rows = Numbered(area.Id(), "A", 20) });

        result.GetProperty("created").GetInt32().Should().Be(20);
        var tables = result.GetProperty("tables").EnumerateArray().ToList();
        tables.Select(t => t.GetProperty("tableCode").GetString()).Should().Contain(["A01", "A20"]);
        tables.Select(t => t.GetProperty("qrToken").GetString()).Distinct().Should().HaveCount(20);
        tables.Should().OnlyContain(t => t.GetProperty("branchId").GetGuid() == _b1 && t.GetProperty("areaName").GetString() == "Tầng 1");
        (await _manager.GetFromJsonAsync<JsonElement>(Tables)).GetArrayLength().Should().Be(20);
    }

    [Fact]
    public async Task One_bad_row_creates_nothing_and_every_bad_row_is_named()
    {
        var area = await _manager.PostOk(Areas, new { name = "Tầng 1" });
        await _manager.PostOk(Tables, new { areaId = area.Id(), tableCode = "A01", capacity = 2 });

        var response = await _manager.Post(Batch, new
        {
            rows = new object[]
            {
                new { areaId = area.Id(), tableCode = "A02", capacity = 2 },
                new { areaId = area.Id(), tableCode = "a01", capacity = 2 },   // already there
                new { areaId = area.Id(), tableCode = "A02", capacity = 2 },   // twice in the file
                new { areaId = area.Id(), tableCode = "A 3", capacity = 2 },
                new { areaId = area.Id(), tableCode = "A04", capacity = 99 },
                new { areaId = Guid.NewGuid(), tableCode = "A05", capacity = 2 },
                new { areaName = "", tableCode = "A06", capacity = 2 },
            },
        });

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        errors.EnumerateObject().Select(e => e.Name).Should().BeEquivalentTo(["Rows[1]", "Rows[2]", "Rows[3]", "Rows[4]", "Rows[5]", "Rows[6]"]);
        errors.GetProperty("Rows[1]")[0].GetString().Should().Contain("Row 2").And.Contain("A01");
        (await _manager.GetFromJsonAsync<JsonElement>(Tables)).GetArrayLength().Should().Be(1);
        await _manager.Post(Batch, new { rows = Array.Empty<object>() }).ShouldBe(400);
    }

    [Fact]
    public async Task An_import_names_areas_and_creates_the_missing_ones()
    {
        var existing = await _manager.PostOk(Areas, new { name = "Tầng 1", sortOrder = 5 });

        var result = await _manager.PostOk(Batch, new
        {
            rows = new object[]
            {
                new { areaName = "tầng 1", tableCode = "T1", capacity = 2 },
                new { areaName = "Sân vườn", tableCode = "G1", capacity = 4 },
                new { areaName = "sân vườn", tableCode = "G2", capacity = 4 },
            },
        });

        result.GetProperty("areasCreated").GetInt32().Should().Be(1);
        var areas = await _manager.GetFromJsonAsync<JsonElement>(Areas);
        areas.GetArrayLength().Should().Be(2);
        areas.EnumerateArray().Single(a => a.Id() == existing.Id()).GetProperty("tableCount").GetInt32().Should().Be(1);
        var garden = areas.EnumerateArray().Single(a => a.Id() != existing.Id());
        (garden.GetProperty("name").GetString(), garden.GetProperty("tableCount").GetInt32(), garden.GetProperty("sortOrder").GetInt32()).Should().Be(("Sân vườn", 2, 6));
    }

    [Fact]
    public async Task A_batch_stays_inside_the_branch_of_the_caller()
    {
        var area = await _admin.PostOk(Areas, new { branchId = _b2, name = "Tầng 1" });

        await _manager.Post(Batch, new { rows = Numbered(area.Id(), "A", 1) }).ShouldBe(400);                                 // an area of another branch
        await _manager.Post(Batch, new { branchId = _b2, rows = Numbered(area.Id(), "A", 1) }).ShouldBe(403, "branch.forbidden");
        await _otherAdmin.Post(Batch, new { branchId = _b2, rows = Numbered(area.Id(), "A", 1) }).ShouldBe(400);              // a branch of another brand
        await _cashier.Post(Batch, new { rows = Numbered(area.Id(), "A", 1) }).ShouldBe(403);

        var created = await _admin.PostOk(Batch, new { branchId = _b2, rows = Numbered(area.Id(), "A", 3) });
        created.GetProperty("created").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task The_plan_limits_the_tables_of_each_branch_but_keeps_the_ones_already_there()
    {
        var area = await _manager.PostOk(Areas, new { name = "Tầng 1" });
        await _manager.PostOk(Batch, new { rows = Numbered(area.Id(), "A", 3) });

        _limits.Current = new TablePlanLimits(true, 4);
        await _manager.Post(Batch, new { rows = Numbered(area.Id(), "B", 2) }).ShouldBe(422, "subscription.table_limit_reached");
        (await _manager.GetFromJsonAsync<JsonElement>(Tables)).GetArrayLength().Should().Be(3);
        await _manager.PostOk(Tables, new { areaId = area.Id(), tableCode = "C1", capacity = 2 });
        await _manager.Post(Tables, new { areaId = area.Id(), tableCode = "C2", capacity = 2 }).ShouldBe(422, "subscription.table_limit_reached");

        var otherArea = await _admin.PostOk(Areas, new { branchId = _b2, name = "Tầng 1" });   // another branch has its own count
        await _admin.PostOk(Batch, new { branchId = _b2, rows = Numbered(otherArea.Id(), "A", 4) });

        _limits.Current = new TablePlanLimits(true, 2);                                         // a smaller plan: what exists keeps working
        (await _manager.GetFromJsonAsync<JsonElement>(Tables)).GetArrayLength().Should().Be(4);
        await _manager.Post(Tables, new { areaId = area.Id(), tableCode = "C3", capacity = 2 }).ShouldBe(422, "subscription.table_limit_reached");
    }

    // ------------------------------------------------------------------ floor plan layout
    private const string Layout = "/api/v1/tables/floor-plan/layout";

    [Fact]
    public async Task Moved_tables_are_saved_together_and_appear_on_the_floor_plan()
    {
        var area = await _manager.PostOk(Areas, new { name = "Tầng 1" });
        var tables = (await _manager.PostOk(Batch, new { rows = Numbered(area.Id(), "A", 2) })).GetProperty("tables").EnumerateArray().Select(t => t.Id()).ToList();

        var saved = await _manager.Put(Layout, new { positions = new[] { new { tableId = tables[0], positionX = 3, positionY = 1 }, new { tableId = tables[1], positionX = 4, positionY = 1 } } });
        saved.IsSuccessStatusCode.Should().BeTrue();

        var plan = await _cashier.GetFromJsonAsync<JsonElement>("/api/v1/tables/floor-plan");
        var first = plan.GetProperty("areas")[0].GetProperty("tables").EnumerateArray().Single(t => t.Id() == tables[0]);
        (first.GetProperty("positionX").GetInt32(), first.GetProperty("positionY").GetInt32()).Should().Be((3, 1));

        // two tables swap places in one save
        (await _manager.Put(Layout, new { positions = new[] { new { tableId = tables[0], positionX = 4, positionY = 1 }, new { tableId = tables[1], positionX = 3, positionY = 1 } } })).IsSuccessStatusCode.Should().BeTrue();
    }

    [Fact]
    public async Task A_layout_refuses_shared_cells_foreign_tables_and_the_wrong_people()
    {
        var area = await _manager.PostOk(Areas, new { name = "Tầng 1" });
        var tables = (await _manager.PostOk(Batch, new { rows = Numbered(area.Id(), "A", 2) })).GetProperty("tables").EnumerateArray().Select(t => t.Id()).ToList();
        var otherArea = await _admin.PostOk(Areas, new { branchId = _b2, name = "Tầng 1" });
        var foreign = (await _admin.PostOk(Batch, new { branchId = _b2, rows = Numbered(otherArea.Id(), "B", 1) })).GetProperty("tables")[0].Id();

        (await _manager.Put(Layout, new { positions = new[] { new { tableId = tables[0], positionX = 1, positionY = 1 }, new { tableId = tables[1], positionX = 1, positionY = 1 } } })).StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        (await _manager.Put(Layout, new { positions = new[] { new { tableId = foreign, positionX = 1, positionY = 1 } } })).StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        (await _manager.Put(Layout, new { positions = new[] { new { tableId = tables[0], positionX = -1, positionY = 1 } } })).StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        (await _manager.Put(Layout, new { positions = Array.Empty<object>() })).StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        (await _cashier.Put(Layout, new { positions = new[] { new { tableId = tables[0], positionX = 1, positionY = 1 } } })).StatusCode.Should().Be(System.Net.HttpStatusCode.Forbidden);

        (await _admin.Put(Layout, new { branchId = _b2, positions = new[] { new { tableId = foreign, positionX = 2, positionY = 0 } } })).IsSuccessStatusCode.Should().BeTrue();
    }

    // ------------------------------------------------------------------ areas (UC-25)
    [Fact]
    public async Task A_branch_manager_adds_areas_to_their_own_branch_and_names_are_unique_per_branch()
    {
        var area = await _manager.PostOk(Areas, new { name = "Tầng 1", sortOrder = 1 });
        area.GetProperty("branchId").GetGuid().Should().Be(_b1);
        area.GetProperty("tableCount").GetInt32().Should().Be(0);

        await _manager.Post(Areas, new { name = "tầng 1" }).ShouldBe(409, "area.duplicate_name");
        await _admin.PostOk(Areas, new { branchId = _b2, name = "Tầng 1" }); // another branch may reuse the name
    }

    [Fact]
    public async Task Area_input_is_validated_and_who_may_write_is_enforced()
    {
        await _manager.Post(Areas, new { name = " ", sortOrder = -1 }).ShouldBe(400);
        await _manager.Post(Areas, new { name = new string('x', 51) }).ShouldBe(400);
        await _manager.Post(Areas, new { name = "X", sortOrder = 10_000 }).ShouldBe(400);
        await _cashier.Post(Areas, new { name = "X" }).ShouldBe(403);
        await _f.As(AicosRoles.Barista, _b1).Post(Areas, new { name = "X" }).ShouldBe(403);
        await _f.As(AicosRoles.Customer).Post(Areas, new { name = "X" }).ShouldBe(403);
        await _f.CreateClient().PostAsJsonAsync(Areas, new { name = "X" }).ShouldBe(401);
    }

    [Fact]
    public async Task A_manager_cannot_use_another_branch_and_the_brand_admin_cannot_use_a_foreign_one()
    {
        await _manager.Post(Areas, new { branchId = _b2, name = "X" }).ShouldBe(403, "branch.forbidden");
        await _admin.Post(Areas, new { branchId = _otherBranch, name = "X" }).ShouldBe(400);   // another brand's branch
        await _admin.Post(Areas, new { branchId = Guid.NewGuid(), name = "X" }).ShouldBe(400);
        await _admin.Post(Areas, new { name = "X" }).ShouldBe(400);                            // the brand admin must say which branch
    }

    [Fact]
    public async Task A_suspended_branch_gets_no_new_areas_or_tables_BR08()
    {
        var area = await _admin.PostOk(Areas, new { branchId = _b2, name = "Tầng 1" });
        _f.Branches.Suspend(_b2);

        await _admin.Post(Areas, new { branchId = _b2, name = "Tầng 2" }).ShouldBe(422, "branch.suspended");
        await _admin.Post(Tables, new { branchId = _b2, areaId = area.Id(), tableCode = "T1", capacity = 2 }).ShouldBe(422, "branch.suspended");
    }

    [Fact]
    public async Task An_area_with_tables_cannot_be_deleted_but_can_be_renamed()
    {
        var area = await _manager.PostOk(Areas, new { name = "Tầng 1" });
        await _manager.PostOk(Tables, new { areaId = area.Id(), tableCode = "T01", capacity = 4 });

        await _manager.DeleteAsync($"{Areas}/{area.Id()}").ShouldBe(409, "area.has_tables");
        var renamed = await _manager.Put($"{Areas}/{area.Id()}", new { name = "Tầng trệt", sortOrder = 2 });
        (await renamed.Body()).GetProperty("name").GetString().Should().Be("Tầng trệt");
        (await _manager.GetFromJsonAsync<JsonElement>($"{Areas}/{area.Id()}")).GetProperty("tableCount").GetInt32().Should().Be(1);

        var empty = await _manager.PostOk(Areas, new { name = "Trống" });
        await _manager.DeleteAsync($"{Areas}/{empty.Id()}").ShouldBe(204);
    }

    [Fact]
    public async Task Areas_are_listed_in_order_for_the_branch_and_staff_see_only_their_own_branch()
    {
        await _manager.PostOk(Areas, new { name = "B", sortOrder = 2 });
        await _manager.PostOk(Areas, new { name = "A", sortOrder = 1 });
        await _admin.PostOk(Areas, new { branchId = _b2, name = "Other branch" });

        (await _cashier.GetFromJsonAsync<JsonElement>(Areas)).EnumerateArray().Select(a => a.GetProperty("name").GetString())
            .Should().Equal("A", "B");
        (await _admin.GetFromJsonAsync<JsonElement>(Areas)).GetArrayLength().Should().Be(3);                  // the whole brand
        (await _admin.GetFromJsonAsync<JsonElement>($"{Areas}?branchId={_b2}")).GetArrayLength().Should().Be(1);
        await _cashier.GetAsync($"{Areas}?branchId={_b2}").ShouldBe(403, "branch.forbidden");
    }

    // ------------------------------------------------------------------ tables (UC-25)
    [Fact]
    public async Task Table_code_is_unique_per_branch_normalised_and_validated()
    {
        var area = await _manager.PostOk(Areas, new { name = "Tầng 1" });

        var table = await _manager.PostOk(Tables, new { areaId = area.Id(), tableCode = "t01", capacity = 4, positionX = 10, positionY = 20 });
        table.GetProperty("tableCode").GetString().Should().Be("T01");
        table.GetProperty("areaName").GetString().Should().Be("Tầng 1");
        table.GetProperty("status").GetString().Should().Be("Available");

        await _manager.Post(Tables, new { areaId = area.Id(), tableCode = "T01", capacity = 2 }).ShouldBe(409, "table.duplicate_code");
        await _manager.Post(Tables, new { areaId = area.Id(), tableCode = "T 02", capacity = 2 }).ShouldBe(400);
        await _manager.Post(Tables, new { areaId = area.Id(), tableCode = "T02", capacity = 0 }).ShouldBe(400);
        await _manager.Post(Tables, new { areaId = area.Id(), tableCode = "T02", capacity = 51 }).ShouldBe(400);
        await _manager.Post(Tables, new { areaId = area.Id(), tableCode = "T02", capacity = 2, positionX = 10_001 }).ShouldBe(400);
        await _manager.Post(Tables, new { tableCode = "T02", capacity = 2 }).ShouldBe(400);                       // area required
        await _cashier.Post(Tables, new { areaId = area.Id(), tableCode = "T03", capacity = 2 }).ShouldBe(403);
    }

    [Fact]
    public async Task A_table_must_sit_in_an_area_of_its_own_branch()
    {
        var mine = await _manager.PostOk(Areas, new { name = "Tầng 1" });
        var otherBranchArea = await _admin.PostOk(Areas, new { branchId = _b2, name = "Tầng 1" });
        var foreignArea = await _otherAdmin.PostOk(Areas, new { branchId = _otherBranch, name = "Foreign" });

        await _manager.Post(Tables, new { areaId = otherBranchArea.Id(), tableCode = "T1", capacity = 2 }).ShouldBe(400);
        await _manager.Post(Tables, new { areaId = foreignArea.Id(), tableCode = "T1", capacity = 2 }).ShouldBe(400);
        await _manager.Post(Tables, new { areaId = Guid.NewGuid(), tableCode = "T1", capacity = 2 }).ShouldBe(400);

        var table = await _manager.PostOk(Tables, new { areaId = mine.Id(), tableCode = "T1", capacity = 2 });
        await _manager.Put($"{Tables}/{table.Id()}", new { areaId = otherBranchArea.Id(), tableCode = "T1", capacity = 2 }).ShouldBe(400);
    }

    [Fact]
    public async Task A_table_can_be_edited_moved_between_areas_and_deleted()
    {
        var a1 = await _manager.PostOk(Areas, new { name = "Tầng 1" });
        var a2 = await _manager.PostOk(Areas, new { name = "Sân vườn" });
        var table = await _manager.PostOk(Tables, new { areaId = a1.Id(), tableCode = "T01", capacity = 4 });

        var edited = await _manager.Put($"{Tables}/{table.Id()}", new { areaId = a2.Id(), tableCode = "G01", capacity = 6, positionX = 100, positionY = 200 });
        var body = await edited.Body();
        body.GetProperty("areaName").GetString().Should().Be("Sân vườn");
        body.GetProperty("tableCode").GetString().Should().Be("G01");
        body.GetProperty("capacity").GetInt32().Should().Be(6);
        body.GetProperty("qrToken").GetString().Should().Be(table.GetProperty("qrToken").GetString()); // editing does not change the QR

        await _manager.DeleteAsync($"{Tables}/{table.Id()}").ShouldBe(204);
        await _manager.GetAsync($"{Tables}/{table.Id()}").ShouldBe(404);
    }

    [Fact]
    public async Task Status_can_be_changed_by_staff_and_an_occupied_table_cannot_be_deleted()
    {
        var area = await _manager.PostOk(Areas, new { name = "Tầng 1" });
        var table = await _manager.PostOk(Tables, new { areaId = area.Id(), tableCode = "T01", capacity = 4 });

        await _cashier.Patch($"{Tables}/{table.Id()}/status", new { status = "Occupied" }).ShouldBe(200);
        await _manager.DeleteAsync($"{Tables}/{table.Id()}").ShouldBe(409, "table.in_use");
        await _cashier.Patch($"{Tables}/{table.Id()}/status", new { }).ShouldBe(400);
        await _cashier.Patch($"{Tables}/{table.Id()}/status", new { status = "OutOfService" }).ShouldBe(200);
        await _manager.DeleteAsync($"{Tables}/{table.Id()}").ShouldBe(204);
    }

    [Fact]
    public async Task The_floor_plan_groups_the_tables_of_a_branch_by_area()
    {
        var a1 = await _manager.PostOk(Areas, new { name = "Tầng 1", sortOrder = 1 });
        var a2 = await _manager.PostOk(Areas, new { name = "Sân vườn", sortOrder = 2 });
        await _manager.PostOk(Tables, new { areaId = a1.Id(), tableCode = "T02", capacity = 4 });
        await _manager.PostOk(Tables, new { areaId = a1.Id(), tableCode = "T01", capacity = 2 });
        await _manager.PostOk(Tables, new { areaId = a2.Id(), tableCode = "G01", capacity = 6 });
        var b2Area = await _admin.PostOk(Areas, new { branchId = _b2, name = "Other" });
        await _admin.PostOk(Tables, new { branchId = _b2, areaId = b2Area.Id(), tableCode = "X1", capacity = 2 });

        var plan = await _cashier.GetFromJsonAsync<JsonElement>("/api/v1/tables/floor-plan");

        plan.GetProperty("branchId").GetGuid().Should().Be(_b1);
        var areas = plan.GetProperty("areas").EnumerateArray().ToList();
        areas.Select(a => a.GetProperty("name").GetString()).Should().Equal("Tầng 1", "Sân vườn");
        areas[0].GetProperty("tables").EnumerateArray().Select(t => t.GetProperty("tableCode").GetString()).Should().Equal("T01", "T02");
        areas[1].GetProperty("tables").GetArrayLength().Should().Be(1);

        (await _admin.GetFromJsonAsync<JsonElement>($"/api/v1/tables/floor-plan?branchId={_b2}")).GetProperty("areas")[0]
            .GetProperty("tables")[0].GetProperty("tableCode").GetString().Should().Be("X1");
        await _admin.GetAsync("/api/v1/tables/floor-plan").ShouldBe(400);                 // the brand admin must pick a branch
        await _cashier.GetAsync($"/api/v1/tables/floor-plan?branchId={_b2}").ShouldBe(403, "branch.forbidden");
    }

    [Fact]
    public async Task The_table_list_filters_by_area_and_status_and_the_brand_admin_sees_every_branch()
    {
        var area = await _manager.PostOk(Areas, new { name = "Tầng 1" });
        var t1 = await _manager.PostOk(Tables, new { areaId = area.Id(), tableCode = "T01", capacity = 4 });
        await _manager.PostOk(Tables, new { areaId = area.Id(), tableCode = "T02", capacity = 4 });
        await _manager.Patch($"{Tables}/{t1.Id()}/status", new { status = "OutOfService" }).ShouldBe(200);
        var b2Area = await _admin.PostOk(Areas, new { branchId = _b2, name = "Tầng 1" });
        await _admin.PostOk(Tables, new { branchId = _b2, areaId = b2Area.Id(), tableCode = "T01", capacity = 2 });

        (await _cashier.GetFromJsonAsync<JsonElement>(Tables)).GetArrayLength().Should().Be(2);
        (await _cashier.GetFromJsonAsync<JsonElement>($"{Tables}?status=OutOfService")).GetArrayLength().Should().Be(1);
        (await _cashier.GetFromJsonAsync<JsonElement>($"{Tables}?areaId={area.Id()}")).GetArrayLength().Should().Be(2);
        (await _admin.GetFromJsonAsync<JsonElement>(Tables)).GetArrayLength().Should().Be(3);
    }

    // ------------------------------------------------------------------ tenant isolation (BR-04) and branch scope (BR-05)
    [Fact]
    public async Task Another_brand_cannot_see_or_change_tables_and_gets_404()
    {
        var area = await _manager.PostOk(Areas, new { name = "Tầng 1" });
        var table = await _manager.PostOk(Tables, new { areaId = area.Id(), tableCode = "T01", capacity = 4 });

        (await _otherAdmin.GetFromJsonAsync<JsonElement>(Tables)).GetArrayLength().Should().Be(0);
        (await _otherAdmin.GetFromJsonAsync<JsonElement>(Areas)).GetArrayLength().Should().Be(0);
        await _otherAdmin.GetAsync($"{Tables}/{table.Id()}").ShouldBe(404);
        await _otherAdmin.Put($"{Tables}/{table.Id()}", new { areaId = area.Id(), tableCode = "X", capacity = 1 }).ShouldBe(404);
        await _otherAdmin.DeleteAsync($"{Tables}/{table.Id()}").ShouldBe(404);
        await _otherAdmin.GetAsync($"{Tables}/{table.Id()}/qr.svg").ShouldBe(404);
        await _otherAdmin.Post($"{Tables}/{table.Id()}/qr/regenerate", new { }).ShouldBe(404);
        await _otherAdmin.GetAsync($"{Areas}/{area.Id()}").ShouldBe(404);
        await _otherAdmin.DeleteAsync($"{Areas}/{area.Id()}").ShouldBe(404);
        await _f.As(AicosRoles.SuperAdmin).GetAsync(Tables).ShouldBe(403);
    }

    [Fact]
    public async Task Staff_of_another_branch_of_the_same_brand_are_forbidden()
    {
        var area = await _manager.PostOk(Areas, new { name = "Tầng 1" });
        var table = await _manager.PostOk(Tables, new { areaId = area.Id(), tableCode = "T01", capacity = 4 });
        var otherManager = _f.As(AicosRoles.BranchManager, _b2);

        await otherManager.GetAsync($"{Tables}/{table.Id()}").ShouldBe(403, "branch.forbidden");
        await otherManager.Put($"{Tables}/{table.Id()}", new { areaId = area.Id(), tableCode = "X", capacity = 1 }).ShouldBe(403, "branch.forbidden");
        await otherManager.DeleteAsync($"{Tables}/{table.Id()}").ShouldBe(403, "branch.forbidden");
        await otherManager.Post($"{Tables}/{table.Id()}/qr/regenerate", new { }).ShouldBe(403, "branch.forbidden");
        await otherManager.GetAsync($"{Areas}/{area.Id()}").ShouldBe(403, "branch.forbidden");
        await _f.As(AicosRoles.Cashier, _b2).Patch($"{Tables}/{table.Id()}/status", new { status = "Occupied" }).ShouldBe(403, "branch.forbidden");
    }

    // ------------------------------------------------------------------ QR codes (UC-26, BR-18)
    [Fact]
    public async Task The_qr_code_points_at_the_table_and_a_scan_resolves_brand_branch_and_table()
    {
        var area = await _manager.PostOk(Areas, new { name = "Tầng 1" });
        var table = await _manager.PostOk(Tables, new { areaId = area.Id(), tableCode = "T01", capacity = 4 });
        var token = table.GetProperty("qrToken").GetString()!;

        token.Should().MatchRegex("^[0-9a-f]{32}$");
        table.GetProperty("qrCodeUrl").GetString().Should().EndWith($"/q/{token}");

        var scan = await _f.CreateClient().GetFromJsonAsync<JsonElement>($"{PublicQr}/{token.ToUpperInvariant()}");   // case-insensitive
        scan.GetProperty("tenantId").GetGuid().Should().Be(_tenant);
        scan.GetProperty("branchId").GetGuid().Should().Be(_b1);
        scan.GetProperty("tableId").GetGuid().Should().Be(table.Id());
        scan.GetProperty("tableCode").GetString().Should().Be("T01");
        scan.GetProperty("areaName").GetString().Should().Be("Tầng 1");
        scan.GetProperty("canOrder").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Regenerating_the_qr_token_invalidates_the_old_code_at_once_BR18()
    {
        var area = await _manager.PostOk(Areas, new { name = "Tầng 1" });
        var table = await _manager.PostOk(Tables, new { areaId = area.Id(), tableCode = "T01", capacity = 4 });
        var oldToken = table.GetProperty("qrToken").GetString();
        var anonymous = _f.CreateClient();
        await anonymous.GetAsync($"{PublicQr}/{oldToken}").ShouldBe(200);

        var regenerated = await _manager.PostOk($"{Tables}/{table.Id()}/qr/regenerate", new { });

        regenerated.GetProperty("qrToken").GetString().Should().NotBe(oldToken);
        await anonymous.GetAsync($"{PublicQr}/{oldToken}").ShouldBe(404);
        await anonymous.GetAsync($"{PublicQr}/{regenerated.GetProperty("qrToken").GetString()}").ShouldBe(200);
        regenerated.GetProperty("qrIssuedAtUtc").GetDateTime().Should().BeOnOrAfter(table.GetProperty("qrIssuedAtUtc").GetDateTime());
    }

    [Fact]
    public async Task Every_table_gets_its_own_token_and_unknown_tokens_are_404()
    {
        var area = await _manager.PostOk(Areas, new { name = "Tầng 1" });
        var tokens = new List<string?>();
        for (var i = 0; i < 5; i++)
        {
            tokens.Add((await _manager.PostOk(Tables, new { areaId = area.Id(), tableCode = $"T{i}", capacity = 2 })).GetProperty("qrToken").GetString());
        }

        tokens.Distinct().Should().HaveCount(5);
        await _f.CreateClient().GetAsync($"{PublicQr}/{new string('0', 32)}").ShouldBe(404);
        await _f.CreateClient().GetAsync($"{PublicQr}/not-a-token").ShouldBe(404);
    }

    [Fact]
    public async Task A_table_out_of_service_still_resolves_but_cannot_take_orders()
    {
        var area = await _manager.PostOk(Areas, new { name = "Tầng 1" });
        var table = await _manager.PostOk(Tables, new { areaId = area.Id(), tableCode = "T01", capacity = 4 });
        await _manager.Patch($"{Tables}/{table.Id()}/status", new { status = "OutOfService" }).ShouldBe(200);

        var scan = await _f.CreateClient().GetFromJsonAsync<JsonElement>($"{PublicQr}/{table.GetProperty("qrToken").GetString()}");

        scan.GetProperty("canOrder").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task The_printable_qr_is_an_svg_that_only_managers_can_download()
    {
        var area = await _manager.PostOk(Areas, new { name = "Tầng 1" });
        var table = await _manager.PostOk(Tables, new { areaId = area.Id(), tableCode = "T01", capacity = 4 });

        var response = await _manager.GetAsync($"{Tables}/{table.Id()}/qr.svg");

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("image/svg+xml");
        (await response.Content.ReadAsStringAsync()).Should().StartWith("<svg").And.Contain("</svg>");
        await _admin.GetAsync($"{Tables}/{table.Id()}/qr.svg").ShouldBe(200);
        await _cashier.GetAsync($"{Tables}/{table.Id()}/qr.svg").ShouldBe(403);
        await _f.CreateClient().GetAsync($"{Tables}/{table.Id()}/qr.svg").ShouldBe(401);
    }

    [Fact]
    public async Task Different_tokens_give_different_printable_codes()
    {
        var area = await _manager.PostOk(Areas, new { name = "Tầng 1" });
        var one = await _manager.PostOk(Tables, new { areaId = area.Id(), tableCode = "T01", capacity = 4 });
        var two = await _manager.PostOk(Tables, new { areaId = area.Id(), tableCode = "T02", capacity = 4 });

        var svgOne = await _manager.GetStringAsync($"{Tables}/{one.Id()}/qr.svg");
        var svgTwo = await _manager.GetStringAsync($"{Tables}/{two.Id()}/qr.svg");

        svgOne.Should().NotBe(svgTwo);
        svgOne.Should().Be(await _manager.GetStringAsync($"{Tables}/{one.Id()}/qr.svg")); // stable until the token changes
    }
}
