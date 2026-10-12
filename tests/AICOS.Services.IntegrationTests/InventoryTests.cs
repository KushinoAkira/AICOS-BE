using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;
using AICOS.InventoryPurchasing.Api;
using AICOS.InventoryPurchasing.Api.Infrastructure;
using AICOS.InventoryPurchasing.Api.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AICOS.Services.IntegrationTests;

public sealed class InventoryTests : IDisposable
{
    private readonly FakeMenuClient _menu = new();
    private readonly ServiceFactory<InventoryPurchasingService, InventoryPurchasingDbContext> _f;
    private readonly Guid _branch = Guid.NewGuid();
    private readonly HttpClient _manager;
    private readonly HttpClient _warehouse;

    public InventoryTests()
    {
        _f = new ServiceFactory<InventoryPurchasingService, InventoryPurchasingDbContext>(s =>
        {
            s.RemoveAll<IMenuClient>();
            s.AddSingleton<IMenuClient>(_menu);
        });
        _manager = _f.As(AicosRoles.Manager, _branch);
        _warehouse = _f.As(AicosRoles.WarehouseStaff, _branch);
    }

    public void Dispose() => _f.Dispose();

    // ------------------------------------------------------------------ suppliers / ingredients
    [Fact]
    public async Task Supplier_rules()
    {
        await _manager.PostOk("/api/v1/inventory/suppliers", new { name = "Trung Nguyên", phone = "0281234567" });
        await _manager.Post("/api/v1/inventory/suppliers", new { name = "trung nguyên", phone = "0281234567" }).ShouldBe(409, "supplier.duplicate_name");
        await _manager.Post("/api/v1/inventory/suppliers", new { name = "No contact" }).ShouldBe(400);
        await _warehouse.Post("/api/v1/inventory/suppliers", new { name = "X", phone = "0281234567" }).ShouldBe(403);
    }

    [Fact]
    public async Task Ingredient_rules_BR14()
    {
        await _manager.Post("/api/v1/inventory/ingredients", new { name = "Sữa", unit = "liter", reorderLevel = 1 }).ShouldBe(400);
        await _manager.Post("/api/v1/inventory/ingredients", new { name = "Sữa", unit = "l", reorderLevel = -1 }).ShouldBe(400);
        var milk = await _manager.PostOk("/api/v1/inventory/ingredients", new { name = "Sữa", unit = "L", reorderLevel = 5 });
        milk.GetProperty("unit").GetString().Should().Be("l");
        milk.GetProperty("quantityOnHand").GetDecimal().Should().Be(0);
        milk.GetProperty("isLowStock").GetBoolean().Should().BeTrue();

        await _manager.Patch($"/api/v1/inventory/ingredients/{milk.Id()}/reorder-level", new { reorderLevel = -2 }).ShouldBe(400);
        await _manager.Patch($"/api/v1/inventory/ingredients/{milk.Id()}/reorder-level", new { reorderLevel = 0 }).ShouldBe(200);
    }

    // ------------------------------------------------------------------ stock movements
    [Fact]
    public async Task Import_then_export_updates_stock_and_writes_ledger()
    {
        var milk = await Ingredient("Sữa", reorder: 5);

        await _warehouse.PostOk("/api/v1/inventory/stock-movements", Movement("Import", "PN-001", milk, 20));
        await _warehouse.PostOk("/api/v1/inventory/stock-movements", Movement("Export", "PX-001", milk, 4));

        (await Get($"/api/v1/inventory/ingredients/{milk}")).GetProperty("quantityOnHand").GetDecimal().Should().Be(16);
        var ledger = await Get($"/api/v1/inventory/stock-movements?ingredientId={milk}");
        ledger.GetProperty("totalCount").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task Export_more_than_stock_is_422_and_the_whole_document_is_rolled_back()
    {
        var milk = await Ingredient("Sữa");
        var sugar = await Ingredient("Đường");
        await _warehouse.PostOk("/api/v1/inventory/stock-movements", Movement("Import", "PN-1", milk, 10));

        await _warehouse.Post("/api/v1/inventory/stock-movements", new
        {
            type = "Export", documentNo = "PX-1",
            lines = new[] { new { ingredientId = milk, quantity = 5m }, new { ingredientId = sugar, quantity = 1m } },
        }).ShouldBe(422, "stock.insufficient");

        (await Get($"/api/v1/inventory/ingredients/{milk}")).GetProperty("quantityOnHand").GetDecimal().Should().Be(10);
    }

    [Fact]
    public async Task Movement_requires_document_number_BR11()
    {
        var milk = await Ingredient("Sữa");
        await _warehouse.Post("/api/v1/inventory/stock-movements", new { type = "Import", lines = new[] { new { ingredientId = milk, quantity = 1m } } }).ShouldBe(400);
    }

    [Fact]
    public async Task Low_stock_alert_is_published_once_when_crossing_the_threshold()
    {
        var milk = await Ingredient("Sữa", reorder: 10);
        await _warehouse.PostOk("/api/v1/inventory/stock-movements", Movement("Import", "PN-1", milk, 20));
        await _warehouse.PostOk("/api/v1/inventory/stock-movements", Movement("Export", "PX-1", milk, 12));
        await _warehouse.PostOk("/api/v1/inventory/stock-movements", Movement("Export", "PX-2", milk, 1));

        _f.Events.Published.OfType<LowStockAlertEvent>().Should().ContainSingle(e => e.IngredientId == milk && e.QuantityOnHand == 8);
        (await _warehouse.GetFromJsonAsync<JsonElement>("/api/v1/inventory/ingredients/low-stock")).GetArrayLength().Should().Be(1);
    }

    // ------------------------------------------------------------------ purchase orders
    [Fact]
    public async Task Purchase_order_flow_create_approve_receive()
    {
        var supplier = (await _manager.PostOk("/api/v1/inventory/suppliers", new { name = "NCC", phone = "0281234567" })).Id();
        var milk = await Ingredient("Sữa");

        var po = await _warehouse.PostOk("/api/v1/inventory/purchase-orders", new
        {
            supplierId = supplier, note = "Giao sáng", items = new[] { new { ingredientId = milk, quantity = 30m, unitPrice = 25000m } },
        });
        po.GetProperty("status").GetString().Should().Be("PendingApproval");
        po.GetProperty("totalAmount").GetDecimal().Should().Be(750000);
        var id = po.Id();

        await _warehouse.Post($"/api/v1/inventory/purchase-orders/{id}/approve", new { }).ShouldBe(403);
        await _warehouse.Post($"/api/v1/inventory/purchase-orders/{id}/receive", new { }).ShouldBe(422, "purchase_order.invalid_status");
        await _manager.Post($"/api/v1/inventory/purchase-orders/{id}/approve", new { }).ShouldBe(200);
        await _warehouse.Post($"/api/v1/inventory/purchase-orders/{id}/receive", new { }).ShouldBe(200);

        (await Get($"/api/v1/inventory/ingredients/{milk}")).GetProperty("quantityOnHand").GetDecimal().Should().Be(30);
        await _manager.Post($"/api/v1/inventory/purchase-orders/{id}/cancel", new { }).ShouldBe(422);
    }

    [Fact]
    public async Task Purchase_order_reject_needs_reason_and_inactive_supplier_is_refused()
    {
        var supplier = (await _manager.PostOk("/api/v1/inventory/suppliers", new { name = "NCC", phone = "0281234567" })).Id();
        var milk = await Ingredient("Sữa");
        var po = (await _warehouse.PostOk("/api/v1/inventory/purchase-orders", new { supplierId = supplier, items = new[] { new { ingredientId = milk, quantity = 1m, unitPrice = 1m } } })).Id();

        await _manager.Post($"/api/v1/inventory/purchase-orders/{po}/reject", new { reason = "" }).ShouldBe(400);
        await _manager.Post($"/api/v1/inventory/purchase-orders/{po}/reject", new { reason = "Giá cao" }).ShouldBe(200);

        await _manager.Patch($"/api/v1/inventory/suppliers/{supplier}/status", new { status = "Inactive" }).ShouldBe(200);
        await _warehouse.Post("/api/v1/inventory/purchase-orders", new { supplierId = supplier, items = new[] { new { ingredientId = milk, quantity = 1m, unitPrice = 1m } } })
            .ShouldBe(400);
    }

    // ------------------------------------------------------------------ stock count / waste
    [Fact]
    public async Task Stock_count_changes_stock_only_after_manager_approval_BR15()
    {
        var milk = await Ingredient("Sữa");
        await _warehouse.PostOk("/api/v1/inventory/stock-movements", Movement("Import", "PN-1", milk, 10));

        var count = await _warehouse.PostOk("/api/v1/inventory/stock-adjustments", new { note = "Kiểm kê tháng", lines = new[] { new { ingredientId = milk, countedQuantity = 8m } } });
        count.GetProperty("lines")[0].GetProperty("difference").GetDecimal().Should().Be(-2);
        (await Get($"/api/v1/inventory/ingredients/{milk}")).GetProperty("quantityOnHand").GetDecimal().Should().Be(10);

        await _warehouse.Post($"/api/v1/inventory/stock-adjustments/{count.Id()}/approve", new { }).ShouldBe(403);
        await _manager.Post($"/api/v1/inventory/stock-adjustments/{count.Id()}/approve", new { }).ShouldBe(200);
        (await Get($"/api/v1/inventory/ingredients/{milk}")).GetProperty("quantityOnHand").GetDecimal().Should().Be(8);
    }

    [Fact]
    public async Task Waste_cannot_exceed_stock_BR31()
    {
        var milk = await Ingredient("Sữa");
        await _warehouse.PostOk("/api/v1/inventory/stock-movements", Movement("Import", "PN-1", milk, 3));

        await _warehouse.Post("/api/v1/inventory/waste-reports", new { ingredientId = milk, quantity = 5m, reason = "Hỏng" }).ShouldBe(422, "BR-31");
        var report = await _warehouse.PostOk("/api/v1/inventory/waste-reports", new { ingredientId = milk, quantity = 1m, reason = "Quá hạn", evidenceImageUrl = "https://img.example.com/1.jpg" });
        report.GetProperty("quantityOnHandAfter").GetDecimal().Should().Be(2);
    }

    // ------------------------------------------------------------------ recipes (calls Menu Service)
    [Fact]
    public async Task Recipe_checks_product_through_menu_service()
    {
        var milk = await Ingredient("Sữa");
        var product = Guid.NewGuid();
        _menu.Products[product] = new MenuProduct(product, _branch, "Latte", 45000, true);
        var foreign = Guid.NewGuid();
        _menu.Products[foreign] = new MenuProduct(foreign, Guid.NewGuid(), "Other", 1, true);

        await _manager.Put($"/api/v1/inventory/recipes/{Guid.NewGuid()}", new { items = new[] { new { ingredientId = milk, quantity = 0.2m } } }).ShouldBe(404);
        await _manager.Put($"/api/v1/inventory/recipes/{foreign}", new { items = new[] { new { ingredientId = milk, quantity = 0.2m } } }).ShouldBe(403);
        await _manager.Put($"/api/v1/inventory/recipes/{product}", new { items = new[] { new { ingredientId = milk, quantity = 0m } } }).ShouldBe(400);

        var recipe = await (await _manager.Put($"/api/v1/inventory/recipes/{product}", new { items = new[] { new { ingredientId = milk, quantity = 0.2m } } })).Body();
        recipe.GetProperty("items")[0].GetProperty("ingredientName").GetString().Should().Be("Sữa");

        _menu.Down = true;
        await _manager.Put($"/api/v1/inventory/recipes/{product}", new { items = Array.Empty<object>() }).ShouldBe(503, "dependency_unavailable");
    }

    private async Task<Guid> Ingredient(string name, decimal reorder = 0) =>
        (await _manager.PostOk("/api/v1/inventory/ingredients", new { name, unit = "l", reorderLevel = reorder })).Id();

    private static object Movement(string type, string doc, Guid ingredient, decimal quantity) =>
        new { type, documentNo = doc, lines = new[] { new { ingredientId = ingredient, quantity } } };

    private Task<JsonElement> Get(string url) => _manager.GetFromJsonAsync<JsonElement>(url);
}

public sealed class FakeMenuClient : IMenuClient
{
    public Dictionary<Guid, MenuProduct> Products { get; } = new();

    public bool Down { get; set; }

    public Task<MenuProduct?> GetProductAsync(Guid productId, CancellationToken ct) =>
        Down ? throw new AICOS.BuildingBlocks.Exceptions.DependencyUnavailableException("Menu") : Task.FromResult(Products.GetValueOrDefault(productId));
}
