using System.Net.Http.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.MenuCatalog.Api;
using AICOS.MenuCatalog.Api.Persistence;

namespace AICOS.Services.IntegrationTests;

public sealed class MenuTests : IDisposable
{
    private readonly ServiceFactory<MenuCatalogService, MenuCatalogDbContext> _f = new();
    private readonly Guid _branch = Guid.NewGuid();
    private readonly HttpClient _manager;

    public MenuTests() => _manager = _f.As(AicosRoles.Manager, _branch);

    public void Dispose() => _f.Dispose();

    // ------------------------------------------------------------------ categories
    [Fact]
    public async Task Manager_creates_category_in_own_branch_and_duplicates_are_409()
    {
        var category = await _manager.PostOk("/api/v1/menu/categories", new { name = "Cà phê", sortOrder = 1 });
        category.GetProperty("branchId").GetGuid().Should().Be(_branch);

        await _manager.Post("/api/v1/menu/categories", new { name = "cà phê" }).ShouldBe(409, "category.duplicate_name");
    }

    [Fact]
    public async Task Category_validation_and_branch_rules()
    {
        await _manager.Post("/api/v1/menu/categories", new { name = " ", sortOrder = -1 }).ShouldBe(400);
        await _manager.Post("/api/v1/menu/categories", new { branchId = Guid.NewGuid(), name = "X" }).ShouldBe(403, "branch.forbidden");
        await _f.As(AicosRoles.SystemAdmin).Post("/api/v1/menu/categories", new { name = "X" }).ShouldBe(400);
        await _f.As(AicosRoles.Cashier, _branch).Post("/api/v1/menu/categories", new { name = "X" }).ShouldBe(403);
    }

    [Fact]
    public async Task Manager_of_another_branch_cannot_touch_the_category()
    {
        var category = await _manager.PostOk("/api/v1/menu/categories", new { name = "Trà" });

        await _f.As(AicosRoles.Manager, Guid.NewGuid()).Put($"/api/v1/menu/categories/{category.Id()}", new { name = "Hack" })
            .ShouldBe(403, "branch.forbidden");
    }

    [Fact]
    public async Task Category_with_products_cannot_be_deleted()
    {
        var category = await _manager.PostOk("/api/v1/menu/categories", new { name = "Bánh" });
        await _manager.PostOk("/api/v1/menu/products", Product(category.Id(), "Croissant"));

        await _manager.DeleteAsync($"/api/v1/menu/categories/{category.Id()}").ShouldBe(409, "category.has_products");
    }

    // ------------------------------------------------------------------ products
    [Fact]
    public async Task Product_with_option_groups_is_created_and_update_replaces_options()
    {
        var category = await _manager.PostOk("/api/v1/menu/categories", new { name = "Cà phê" });
        var product = await _manager.PostOk("/api/v1/menu/products", Product(category.Id(), "Bạc xỉu"));
        product.GetProperty("optionGroups").GetArrayLength().Should().Be(2);

        var updated = await _manager.Put($"/api/v1/menu/products/{product.Id()}", new
        {
            categoryId = category.Id(), name = "Bạc xỉu", basePrice = 35000,
            optionGroups = new[] { new { name = "Size", selectionType = "Single", isRequired = true, options = new[] { new { name = "M", priceDelta = 0, isDefault = true } } } },
        });
        (await updated.Body()).GetProperty("optionGroups").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Product_validation_errors()
    {
        var category = await _manager.PostOk("/api/v1/menu/categories", new { name = "Cà phê" });

        var response = await _manager.Post("/api/v1/menu/products", new
        {
            categoryId = category.Id(), name = "", basePrice = 0, imageUrl = "not a url",
            optionGroups = new[]
            {
                new { name = "Size", selectionType = "Single", isRequired = true, options = new[] { new { name = "M", priceDelta = 0, isDefault = true }, new { name = "L", priceDelta = 5000, isDefault = true } } },
            },
        });

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        var errors = (await response.Body()).GetProperty("errors");
        errors.TryGetProperty("Name", out _).Should().BeTrue();
        errors.TryGetProperty("BasePrice", out _).Should().BeTrue();
        errors.TryGetProperty("ImageUrl", out _).Should().BeTrue();
        errors.TryGetProperty("OptionGroups[0].Options", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Product_in_category_of_other_branch_is_rejected()
    {
        var otherManager = _f.As(AicosRoles.Manager, Guid.NewGuid());
        var foreign = await otherManager.PostOk("/api/v1/menu/categories", new { name = "Khác" });

        await _manager.Post("/api/v1/menu/products", Product(foreign.Id(), "X")).ShouldBe(400);
    }

    [Fact]
    public async Task Cashier_can_mark_sold_out_and_deleted_product_disappears()
    {
        var category = await _manager.PostOk("/api/v1/menu/categories", new { name = "Cà phê" });
        var product = await _manager.PostOk("/api/v1/menu/products", Product(category.Id(), "Cold brew"));

        await _f.As(AicosRoles.Cashier, _branch).Patch($"/api/v1/menu/products/{product.Id()}/availability", new { isAvailable = false }).ShouldBe(200);
        await _manager.DeleteAsync($"/api/v1/menu/products/{product.Id()}").ShouldBe(204);
        await _manager.GetAsync($"/api/v1/menu/products/{product.Id()}").ShouldBe(404);
    }

    // ------------------------------------------------------------------ promotions + public menu
    [Fact]
    public async Task Promotion_rules_BR26_percentage_and_branch()
    {
        var category = await _manager.PostOk("/api/v1/menu/categories", new { name = "Cà phê" });
        var product = await _manager.PostOk("/api/v1/menu/products", Product(category.Id(), "Latte"));
        var now = _f.Clock.Now;

        await _manager.Post("/api/v1/menu/promotions", Promo(product.Id(), now.AddDays(5), now.AddDays(1))).ShouldBe(400);
        await _manager.Post("/api/v1/menu/promotions", Promo(product.Id(), now, now.AddDays(1), value: 150)).ShouldBe(400);
        await _manager.Post("/api/v1/menu/promotions", Promo(Guid.NewGuid(), now, now.AddDays(1))).ShouldBe(400);
        await _manager.PostOk("/api/v1/menu/promotions", Promo(product.Id(), now.AddHours(-1), now.AddDays(1)));
    }

    [Fact]
    public async Task Public_menu_is_anonymous_hides_hidden_and_unavailable_and_applies_promotion()
    {
        var coffee = await _manager.PostOk("/api/v1/menu/categories", new { name = "Cà phê" });
        var hidden = await _manager.PostOk("/api/v1/menu/categories", new { name = "Ẩn" });
        var latte = await _manager.PostOk("/api/v1/menu/products", Product(coffee.Id(), "Latte"));
        var soldOut = await _manager.PostOk("/api/v1/menu/products", Product(coffee.Id(), "Mocha"));
        await _manager.PostOk("/api/v1/menu/products", Product(hidden.Id(), "Secret"));
        await _manager.Patch($"/api/v1/menu/categories/{hidden.Id()}/status", new { status = "Hidden" }).ShouldBe(200);
        await _manager.Patch($"/api/v1/menu/products/{soldOut.Id()}/availability", new { isAvailable = false }).ShouldBe(200);
        await _manager.PostOk("/api/v1/menu/promotions", Promo(latte.Id(), _f.Clock.Now.AddHours(-1), _f.Clock.Now.AddDays(1), value: 20));

        var menu = await _f.CreateClient().GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/v1/menu/public/branches/{_branch}/menu");

        var categories = menu.GetProperty("categories");
        categories.GetArrayLength().Should().Be(1);
        var products = categories[0].GetProperty("products");
        products.GetArrayLength().Should().Be(1);
        products[0].GetProperty("price").GetDecimal().Should().Be(45000);
        products[0].GetProperty("promotionPrice").GetDecimal().Should().Be(36000);
    }

    private static object Product(Guid categoryId, string name) => new
    {
        categoryId, name, description = "Ngon", basePrice = 45000, tags = new[] { "best-seller" },
        optionGroups = new object[]
        {
            new { name = "Size", selectionType = "Single", isRequired = true, options = new[] { new { name = "M", priceDelta = 0, isDefault = true }, new { name = "L", priceDelta = 5000, isDefault = false } } },
            new { name = "Topping", selectionType = "Multiple", isRequired = false, options = new[] { new { name = "Trân châu", priceDelta = 5000, isDefault = false } } },
        },
    };

    private static object Promo(Guid productId, DateTimeOffset start, DateTimeOffset end, decimal value = 10) => new
    {
        name = "Giảm giá", discountType = "Percentage", discountValue = value, startDate = start, endDate = end, productIds = new[] { productId },
    };
}
