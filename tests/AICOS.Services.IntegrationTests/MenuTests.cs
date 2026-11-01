using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;
using AICOS.MenuCatalog.Api;
using AICOS.MenuCatalog.Api.Persistence;

namespace AICOS.Services.IntegrationTests;

/// <summary>
/// UC-14 menu of the brand, UC-15 publishing to branches, UC-24 sold out, branch prices (BR-15) and tenant isolation (BR-04).
/// </summary>
public sealed class MenuTests : IDisposable
{
    private const string Menu = "/api/v1/menu";

    private readonly ServiceFactory<MenuCatalogService, MenuCatalogDbContext> _f = new();
    private readonly Guid _tenant = ServiceFactory<MenuCatalogService, MenuCatalogDbContext>.DefaultTenantId;
    private readonly Guid _otherTenant = Guid.NewGuid();
    private readonly Guid _b1;
    private readonly Guid _b2;
    private readonly Guid _otherBranch;
    private readonly HttpClient _admin;
    private readonly HttpClient _otherAdmin;

    public MenuTests()
    {
        _b1 = _f.Branches.Add(_tenant, name: "Quận 1");
        _b2 = _f.Branches.Add(_tenant, name: "Quận 3");
        _otherBranch = _f.Branches.Add(_otherTenant, name: "Other brand branch");
        _admin = _f.As(AicosRoles.BrandAdmin);
        _otherAdmin = _f.As(AicosRoles.BrandAdmin, tenantId: _otherTenant);
    }

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task The_product_list_shows_branch_prices_and_clearing_one_goes_back_to_the_base_price()
    {
        var category = await _admin.PostOk($"{Menu}/categories", new { name = "Cà phê", sortOrder = 1 });
        var product = await _admin.PostOk($"{Menu}/products", new { categoryId = category.Id(), name = "Bạc xỉu", basePrice = 30_000 });
        await _admin.Put($"{Menu}/products/{product.Id()}/branches/{_b1}", new { isPublished = true, priceOverride = 42_000 });
        await _admin.Put($"{Menu}/products/{product.Id()}/branches/{_b2}", new { isPublished = false, priceOverride = 35_000 });

        var all = (await _admin.GetFromJsonAsync<JsonElement>($"{Menu}/products")).GetProperty("items")[0].GetProperty("branchPrices");
        all.EnumerateArray().Select(p => (p.GetProperty("branchId").GetGuid(), p.GetProperty("price").GetDecimal())).Should().BeEquivalentTo([(_b1, 42_000m), (_b2, 35_000m)]);

        var cashier = _f.As(AicosRoles.Cashier, _b1);
        (await cashier.GetFromJsonAsync<JsonElement>($"{Menu}/products")).GetProperty("items")[0].GetProperty("branchPrices").EnumerateArray().Should().ContainSingle();   // only their own

        await _admin.Put($"{Menu}/products/{product.Id()}/branches/{_b1}", new { isPublished = true, priceOverride = (decimal?)null });
        var left = (await _admin.GetFromJsonAsync<JsonElement>($"{Menu}/products")).GetProperty("items")[0].GetProperty("branchPrices");
        left.EnumerateArray().Single().GetProperty("branchId").GetGuid().Should().Be(_b2);
        var settings = await _admin.GetFromJsonAsync<JsonElement>($"{Menu}/products/{product.Id()}/branches");
        settings.EnumerateArray().Single(s => s.GetProperty("branchId").GetGuid() == _b1).GetProperty("effectivePrice").GetDecimal().Should().Be(30_000);
    }

    // ------------------------------------------------------------------ categories
    [Fact]
    public async Task Brand_admin_creates_a_category_for_the_whole_brand_and_duplicates_are_409()
    {
        var category = await _admin.PostOk($"{Menu}/categories", new { name = "Cà phê", sortOrder = 1 });
        category.GetProperty("name").GetString().Should().Be("Cà phê");
        category.GetProperty("status").GetString().Should().Be("Active");

        await _admin.Post($"{Menu}/categories", new { name = "cà phê" }).ShouldBe(409, "category.duplicate_name");
    }

    [Fact]
    public async Task Category_input_is_validated()
    {
        await _admin.Post($"{Menu}/categories", new { name = " ", sortOrder = -1 }).ShouldBe(400);
        await _admin.Post($"{Menu}/categories", new { name = new string('x', 101) }).ShouldBe(400);
        await _admin.Post($"{Menu}/categories", new { name = "X", sortOrder = 10_000 }).ShouldBe(400);
    }

    [Theory]
    [InlineData(AicosRoles.BranchManager)]
    [InlineData(AicosRoles.Barista)]
    [InlineData(AicosRoles.Cashier)]
    [InlineData(AicosRoles.Customer)]
    public async Task Only_the_brand_admin_changes_the_menu_BR12(string role)
    {
        var category = await _admin.PostOk($"{Menu}/categories", new { name = "Trà" });
        var product = await _admin.PostOk($"{Menu}/products", Coffee(category.Id(), "Trà đào"));
        var client = _f.As(role, _b1);

        await client.Post($"{Menu}/categories", new { name = "X" }).ShouldBe(403);
        await client.Put($"{Menu}/categories/{category.Id()}", new { name = "X" }).ShouldBe(403);
        await client.DeleteAsync($"{Menu}/categories/{category.Id()}").ShouldBe(403);
        await client.Post($"{Menu}/products", Coffee(category.Id(), "Hack")).ShouldBe(403);
        await client.Put($"{Menu}/products/{product.Id()}", Coffee(category.Id(), "Hack")).ShouldBe(403);
        await client.DeleteAsync($"{Menu}/products/{product.Id()}").ShouldBe(403);
        await client.Put($"{Menu}/products/{product.Id()}/branches/{_b1}", new { isPublished = true }).ShouldBe(403);
        await client.Post($"{Menu}/publications", new { branchIds = new[] { _b1 } }).ShouldBe(403);
    }

    [Theory]
    [InlineData(AicosRoles.BranchManager)]
    [InlineData(AicosRoles.Barista)]
    [InlineData(AicosRoles.Cashier)]
    public async Task Staff_of_the_brand_can_read_the_brand_menu(string role)
    {
        var category = await _admin.PostOk($"{Menu}/categories", new { name = "Trà" });
        var product = await _admin.PostOk($"{Menu}/products", Coffee(category.Id(), "Trà đào"));
        var client = _f.As(role, _b1);

        (await client.GetFromJsonAsync<JsonElement>($"{Menu}/categories")).GetArrayLength().Should().Be(1);
        (await client.GetFromJsonAsync<JsonElement>($"{Menu}/products")).GetProperty("totalCount").GetInt32().Should().Be(1);
        await client.GetAsync($"{Menu}/products/{product.Id()}").ShouldBe(200);
    }

    [Fact]
    public async Task The_super_admin_and_anonymous_callers_cannot_read_the_brand_menu()
    {
        await _f.As(AicosRoles.SuperAdmin).GetAsync($"{Menu}/categories").ShouldBe(403);
        await _f.As(AicosRoles.SuperAdmin).GetAsync($"{Menu}/products").ShouldBe(403);
        await _f.CreateClient().GetAsync($"{Menu}/categories").ShouldBe(401);
    }

    [Fact]
    public async Task Category_with_products_cannot_be_deleted_but_an_empty_one_can()
    {
        var category = await _admin.PostOk($"{Menu}/categories", new { name = "Bánh" });
        await _admin.PostOk($"{Menu}/products", Coffee(category.Id(), "Croissant"));
        var empty = await _admin.PostOk($"{Menu}/categories", new { name = "Trống" });

        await _admin.DeleteAsync($"{Menu}/categories/{category.Id()}").ShouldBe(409, "category.has_products");
        await _admin.DeleteAsync($"{Menu}/categories/{empty.Id()}").ShouldBe(204);
        await _admin.GetAsync($"{Menu}/categories/{empty.Id()}").ShouldBe(404);
    }

    // ------------------------------------------------------------------ tenant isolation (BR-04)
    [Fact]
    public async Task Another_brand_cannot_see_or_change_the_menu_and_gets_404_not_403()
    {
        var category = await _admin.PostOk($"{Menu}/categories", new { name = "Cà phê" });
        var product = await _admin.PostOk($"{Menu}/products", Coffee(category.Id(), "Bạc xỉu"));

        (await _otherAdmin.GetFromJsonAsync<JsonElement>($"{Menu}/categories")).GetArrayLength().Should().Be(0);
        (await _otherAdmin.GetFromJsonAsync<JsonElement>($"{Menu}/products")).GetProperty("totalCount").GetInt32().Should().Be(0);
        await _otherAdmin.GetAsync($"{Menu}/categories/{category.Id()}").ShouldBe(404);
        await _otherAdmin.Put($"{Menu}/categories/{category.Id()}", new { name = "Hack" }).ShouldBe(404);
        await _otherAdmin.DeleteAsync($"{Menu}/categories/{category.Id()}").ShouldBe(404);
        await _otherAdmin.GetAsync($"{Menu}/products/{product.Id()}").ShouldBe(404);
        await _otherAdmin.DeleteAsync($"{Menu}/products/{product.Id()}").ShouldBe(404);
        await _otherAdmin.Put($"{Menu}/products/{product.Id()}/branches/{_otherBranch}", new { isPublished = true }).ShouldBe(404);

        // Their own data is independent: the same names are fine in another brand.
        var theirs = await _otherAdmin.PostOk($"{Menu}/categories", new { name = "Cà phê" });
        await _otherAdmin.Post($"{Menu}/products", Coffee(theirs.Id(), "Bạc xỉu")).ShouldBe(201);
        // ...but a product cannot be filed under a category of the other brand.
        await _otherAdmin.Post($"{Menu}/products", Coffee(category.Id(), "Sneaky")).ShouldBe(400);
    }

    // ------------------------------------------------------------------ products and options (UC-14, BR-13, BR-14)
    [Fact]
    public async Task A_product_keeps_its_size_sugar_ice_and_topping_groups_and_an_update_replaces_them()
    {
        var category = await _admin.PostOk($"{Menu}/categories", new { name = "Cà phê" });

        var product = await _admin.PostOk($"{Menu}/products", Coffee(category.Id(), "Bạc xỉu"));

        product.GetProperty("basePrice").GetDecimal().Should().Be(35_000);
        var groups = product.GetProperty("optionGroups").EnumerateArray().ToList();
        groups.Select(g => g.GetProperty("kind").GetString()).Should().Equal("Size", "Sugar", "Ice", "Topping");
        groups[0].GetProperty("selectionType").GetString().Should().Be("Single");
        groups[0].GetProperty("options").EnumerateArray().Select(o => o.GetProperty("priceDelta").GetDecimal()).Should().Equal(0m, 5_000m, 10_000m);
        groups[3].GetProperty("maxSelections").GetInt32().Should().Be(2);

        var updated = await _admin.Put($"{Menu}/products/{product.Id()}", new
        {
            categoryId = category.Id(), name = "Bạc xỉu đá", basePrice = 40_000, tags = new[] { "best-seller" },
            optionGroups = new[]
            {
                new { name = "Size", kind = "Size", selectionType = "Single", isRequired = true, maxSelections = (int?)null, sortOrder = (int?)0,
                      options = new[] { new { name = "M", priceDelta = 0, isDefault = true, requiresGroup = (string?)null, requiresOption = (string?)null } } },
            },
        });
        updated.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var body = await updated.Body();
        body.GetProperty("name").GetString().Should().Be("Bạc xỉu đá");
        body.GetProperty("optionGroups").GetArrayLength().Should().Be(1);
        body.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).Should().Equal("best-seller");
    }

    [Fact]
    public async Task A_topping_can_require_another_option_of_the_same_product_BR14()
    {
        var category = await _admin.PostOk($"{Menu}/categories", new { name = "Cà phê" });

        var product = await _admin.PostOk($"{Menu}/products", new
        {
            categoryId = category.Id(), name = "Cà phê sữa", basePrice = 30_000,
            optionGroups = new object[]
            {
                new { name = "Ice", kind = "Ice", selectionType = "Single", isRequired = true,
                      options = new object[] { new { name = "Iced", priceDelta = 0 }, new { name = "Hot", priceDelta = 0 } } },
                new { name = "Topping", kind = "Topping", selectionType = "Multiple", isRequired = false,
                      options = new object[]
                      {
                          new { name = "Pudding", priceDelta = 8_000 },
                          new { name = "Cold foam", priceDelta = 10_000, requiresGroup = "Ice", requiresOption = "iced" },
                      } },
            },
        });

        var groups = product.GetProperty("optionGroups").EnumerateArray().ToList();
        var iced = groups[0].GetProperty("options").EnumerateArray().Single(o => o.GetProperty("name").GetString() == "Iced");
        var foam = groups[1].GetProperty("options").EnumerateArray().Single(o => o.GetProperty("name").GetString() == "Cold foam");
        foam.GetProperty("requiresOptionId").GetGuid().Should().Be(iced.Id());
        groups[1].GetProperty("options").EnumerateArray().Single(o => o.GetProperty("name").GetString() == "Pudding")
            .GetProperty("requiresOptionId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Bad_option_dependencies_are_rejected()
    {
        var category = await _admin.PostOk($"{Menu}/categories", new { name = "Cà phê" });
        object WithTopping(object topping) => new
        {
            categoryId = category.Id(), name = $"P{Guid.NewGuid():N}", basePrice = 30_000,
            optionGroups = new object[]
            {
                new { name = "Ice", kind = "Ice", selectionType = "Single", options = new object[] { new { name = "Iced", priceDelta = 0 }, new { name = "Hot", priceDelta = 0, requiresGroup = "Topping", requiresOption = "Foam" } } },
                new { name = "Topping", kind = "Topping", selectionType = "Multiple", options = new[] { topping } },
            },
        };

        // Unknown target, half a reference, its own group, and a chain of dependencies.
        await _admin.Post($"{Menu}/products", WithTopping(new { name = "Foam", priceDelta = 1, requiresGroup = "Ice", requiresOption = "Frozen" })).ShouldBe(400);
        await _admin.Post($"{Menu}/products", WithTopping(new { name = "Foam", priceDelta = 1, requiresGroup = "Ice" })).ShouldBe(400);
        await _admin.Post($"{Menu}/products", WithTopping(new { name = "Foam", priceDelta = 1, requiresGroup = "Topping", requiresOption = "Foam" })).ShouldBe(400);
        await _admin.Post($"{Menu}/products", WithTopping(new { name = "Foam", priceDelta = 1, requiresGroup = "Ice", requiresOption = "Hot" })).ShouldBe(400); // Hot itself requires Foam
    }

    [Fact]
    public async Task Product_and_option_rules_are_validated()
    {
        var category = await _admin.PostOk($"{Menu}/categories", new { name = "Cà phê" });
        object Product(object? price = null, object? groups = null, object? tags = null) => new
        {
            categoryId = category.Id(), name = $"P{Guid.NewGuid():N}", basePrice = price ?? 30_000, tags, optionGroups = groups,
        };
        object Group(string name, string kind, string selection, int? max = null, params object[] options) => new
        {
            name, kind, selectionType = selection, maxSelections = max, options = options.Length == 0 ? new object[] { new { name = "A", priceDelta = 0 } } : options,
        };

        await _admin.Post($"{Menu}/products", Product(price: 0)).ShouldBe(400);                                          // BR-13
        await _admin.Post($"{Menu}/products", Product(price: -5)).ShouldBe(400);
        await _admin.Post($"{Menu}/products", Product(price: 100_000_001)).ShouldBe(400);
        await _admin.Post($"{Menu}/products", new { categoryId = Guid.NewGuid(), name = "X", basePrice = 1000 }).ShouldBe(400);  // unknown category
        await _admin.Post($"{Menu}/products", Product(tags: Enumerable.Range(0, 11).Select(i => $"t{i}").ToArray())).ShouldBe(400);
        await _admin.Post($"{Menu}/products", Product(groups: new[] { Group("Size", "Size", "Multiple") })).ShouldBe(400);           // size is single-choice
        await _admin.Post($"{Menu}/products", Product(groups: new[] { Group("Sugar", "Sugar", "Multiple") })).ShouldBe(400);
        await _admin.Post($"{Menu}/products", Product(groups: new[] { Group("S1", "Size", "Single"), Group("S2", "Size", "Single") })).ShouldBe(400); // one size group
        await _admin.Post($"{Menu}/products", Product(groups: new[] { Group("Topping", "Topping", "Single", max: 2) })).ShouldBe(400);               // max only for multiple
        await _admin.Post($"{Menu}/products", Product(groups: new[] { Group("Topping", "Topping", "Multiple", max: 5) })).ShouldBe(400);              // more than the options
        await _admin.Post($"{Menu}/products", Product(groups: new[] { Group("Topping", "Topping", "Multiple", max: 0) })).ShouldBe(400);
        await _admin.Post($"{Menu}/products", Product(groups: new[]
        {
            Group("Topping", "Topping", "Multiple", null, new { name = "A", priceDelta = -1 }),                                                        // BR-13
        })).ShouldBe(400);
        await _admin.Post($"{Menu}/products", Product(groups: new[]
        {
            Group("Size", "Size", "Single", null, new { name = "M", priceDelta = 0, isDefault = true }, new { name = "L", priceDelta = 1, isDefault = true }),
        })).ShouldBe(400);                                                                                                                              // two defaults
        await _admin.Post($"{Menu}/products", Product(groups: new[]
        {
            Group("Topping", "Topping", "Multiple", null, new { name = "A", priceDelta = 0 }, new { name = "a", priceDelta = 0 }),
        })).ShouldBe(400);                                                                                                                              // duplicate names
    }

    [Fact]
    public async Task Product_names_are_unique_within_the_brand()
    {
        var category = await _admin.PostOk($"{Menu}/categories", new { name = "Cà phê" });
        await _admin.PostOk($"{Menu}/products", Coffee(category.Id(), "Bạc xỉu"));

        await _admin.Post($"{Menu}/products", Coffee(category.Id(), "BẠC XỈU")).ShouldBe(409, "product.duplicate_name");
    }

    [Fact]
    public async Task A_deleted_product_disappears_everywhere_but_can_be_recreated_under_the_same_name()
    {
        var category = await _admin.PostOk($"{Menu}/categories", new { name = "Cà phê" });
        var product = await PublishedProductAsync(category.Id(), "Bạc xỉu", _b1);

        await _admin.DeleteAsync($"{Menu}/products/{product.Id()}").ShouldBe(204);

        await _admin.GetAsync($"{Menu}/products/{product.Id()}").ShouldBe(404);
        (await _admin.GetFromJsonAsync<JsonElement>($"{Menu}/products")).GetProperty("totalCount").GetInt32().Should().Be(0);
        (await PublicMenuAsync(_b1)).GetProperty("categories").GetArrayLength().Should().Be(0);
        await _admin.Post($"{Menu}/products", Coffee(category.Id(), "Bạc xỉu")).ShouldBe(201);
    }

    // ------------------------------------------------------------------ publishing and branch price (UC-15, BR-15)
    [Fact]
    public async Task A_product_is_only_on_the_menus_of_the_branches_it_was_published_to()
    {
        var category = await _admin.PostOk($"{Menu}/categories", new { name = "Cà phê" });
        var product = await _admin.PostOk($"{Menu}/products", Coffee(category.Id(), "Bạc xỉu"));
        (await PublicMenuAsync(_b1)).GetProperty("categories").GetArrayLength().Should().Be(0); // not published yet

        await _admin.Put($"{Menu}/products/{product.Id()}/branches/{_b1}", new { isPublished = true }).ShouldBe(200);

        Products(await PublicMenuAsync(_b1)).Select(p => p.GetProperty("name").GetString()).Should().Equal("Bạc xỉu");
        Products(await PublicMenuAsync(_b2)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_branch_price_replaces_the_base_price_only_at_that_branch_BR15()
    {
        var category = await _admin.PostOk($"{Menu}/categories", new { name = "Cà phê" });
        var product = await PublishedProductAsync(category.Id(), "Bạc xỉu", _b1, _b2);

        var response = await _admin.Put($"{Menu}/products/{product.Id()}/branches/{_b1}", new { isPublished = true, priceOverride = 42_000 });
        var setting = await response.Body();
        setting.GetProperty("basePrice").GetDecimal().Should().Be(35_000);
        setting.GetProperty("effectivePrice").GetDecimal().Should().Be(42_000);

        var atB1 = Products(await PublicMenuAsync(_b1)).Single();
        atB1.GetProperty("price").GetDecimal().Should().Be(42_000);
        atB1.GetProperty("hasBranchPrice").GetBoolean().Should().BeTrue();
        var atB2 = Products(await PublicMenuAsync(_b2)).Single();
        atB2.GetProperty("price").GetDecimal().Should().Be(35_000);
        atB2.GetProperty("hasBranchPrice").GetBoolean().Should().BeFalse();

        // Clearing the override goes back to the base price; changing the base price reaches branches without an override.
        await _admin.Put($"{Menu}/products/{product.Id()}/branches/{_b1}", new { isPublished = true }).ShouldBe(200);
        Products(await PublicMenuAsync(_b1)).Single().GetProperty("price").GetDecimal().Should().Be(35_000);
        await _admin.Put($"{Menu}/products/{product.Id()}", Coffee(category.Id(), "Bạc xỉu", price: 38_000)).ShouldBe(200);
        Products(await PublicMenuAsync(_b2)).Single().GetProperty("price").GetDecimal().Should().Be(38_000);
    }

    [Fact]
    public async Task Branch_settings_are_validated_and_must_stay_inside_the_brand()
    {
        var category = await _admin.PostOk($"{Menu}/categories", new { name = "Cà phê" });
        var product = await _admin.PostOk($"{Menu}/products", Coffee(category.Id(), "Bạc xỉu"));
        var url = $"{Menu}/products/{product.Id()}/branches";

        await _admin.Put($"{url}/{_b1}", new { }).ShouldBe(400);                                           // isPublished required
        await _admin.Put($"{url}/{_b1}", new { isPublished = true, priceOverride = 0 }).ShouldBe(400);
        await _admin.Put($"{url}/{_b1}", new { isPublished = true, priceOverride = 100_000_001 }).ShouldBe(400);
        await _admin.Put($"{url}/{_otherBranch}", new { isPublished = true }).ShouldBe(400);               // a branch of another brand
        await _admin.Put($"{url}/{Guid.NewGuid()}", new { isPublished = true }).ShouldBe(400);             // no such branch
        await _admin.Put($"{Menu}/products/{Guid.NewGuid()}/branches/{_b1}", new { isPublished = true }).ShouldBe(404);
    }

    [Fact]
    public async Task A_suspended_branch_cannot_get_new_products_but_can_be_cleaned_up_BR08()
    {
        var category = await _admin.PostOk($"{Menu}/categories", new { name = "Cà phê" });
        var product = await PublishedProductAsync(category.Id(), "Bạc xỉu", _b1);
        _f.Branches.Suspend(_b1);

        await _admin.Put($"{Menu}/products/{product.Id()}/branches/{_b1}", new { isPublished = true, priceOverride = 40_000 }).ShouldBe(422, "branch.suspended");
        await _admin.Post($"{Menu}/publications", new { branchIds = new[] { _b1 } }).ShouldBe(422, "branch.suspended");
        await _admin.Put($"{Menu}/products/{product.Id()}/branches/{_b1}", new { isPublished = false }).ShouldBe(200); // taking it off is fine
    }

    [Fact]
    public async Task Publishing_in_bulk_covers_several_branches_and_is_idempotent()
    {
        var category = await _admin.PostOk($"{Menu}/categories", new { name = "Cà phê" });
        await _admin.PostOk($"{Menu}/products", Coffee(category.Id(), "Bạc xỉu"));
        await _admin.PostOk($"{Menu}/products", Coffee(category.Id(), "Cà phê đen"));

        var first = await _admin.Post($"{Menu}/publications", new { branchIds = new[] { _b1, _b2 } });
        (await first.Body()).Should().Match<JsonElement>(r =>
            r.GetProperty("branches").GetInt32() == 2 && r.GetProperty("products").GetInt32() == 2 && r.GetProperty("changed").GetInt32() == 4);
        Products(await PublicMenuAsync(_b1)).Should().HaveCount(2);
        Products(await PublicMenuAsync(_b2)).Should().HaveCount(2);

        var again = await _admin.Post($"{Menu}/publications", new { branchIds = new[] { _b1, _b2 } });
        (await again.Body()).GetProperty("changed").GetInt32().Should().Be(0);

        var off = await _admin.Post($"{Menu}/publications", new { branchIds = new[] { _b1 }, publish = false });
        (await off.Body()).GetProperty("changed").GetInt32().Should().Be(2);
        Products(await PublicMenuAsync(_b1)).Should().BeEmpty();
        Products(await PublicMenuAsync(_b2)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Bulk_publishing_can_pick_products_and_validates_its_input()
    {
        var category = await _admin.PostOk($"{Menu}/categories", new { name = "Cà phê" });
        var one = await _admin.PostOk($"{Menu}/products", Coffee(category.Id(), "Bạc xỉu"));
        await _admin.PostOk($"{Menu}/products", Coffee(category.Id(), "Cà phê đen"));
        var other = await _otherAdmin.PostOk($"{Menu}/categories", new { name = "Trà" });
        var foreign = await _otherAdmin.PostOk($"{Menu}/products", Coffee(other.Id(), "Trà đá"));

        await _admin.Post($"{Menu}/publications", new { branchIds = new[] { _b1 }, productIds = new[] { one.Id() } }).ShouldBe(200);
        Products(await PublicMenuAsync(_b1)).Select(p => p.GetProperty("name").GetString()).Should().Equal("Bạc xỉu");

        await _admin.Post($"{Menu}/publications", new { }).ShouldBe(400);                                                       // no branches
        await _admin.Post($"{Menu}/publications", new { branchIds = Array.Empty<Guid>() }).ShouldBe(400);
        await _admin.Post($"{Menu}/publications", new { branchIds = Enumerable.Range(0, 51).Select(_ => Guid.NewGuid()).ToArray() }).ShouldBe(400);
        await _admin.Post($"{Menu}/publications", new { branchIds = new[] { _b1 }, productIds = Array.Empty<Guid>() }).ShouldBe(400);
        await _admin.Post($"{Menu}/publications", new { branchIds = new[] { _b1 }, productIds = new[] { foreign.Id() } }).ShouldBe(400); // another brand's product
        await _admin.Post($"{Menu}/publications", new { branchIds = new[] { _b1, _otherBranch } }).ShouldBe(400);                    // another brand's branch
    }

    [Fact]
    public async Task Hidden_categories_are_off_every_menu_until_shown_again()
    {
        var category = await _admin.PostOk($"{Menu}/categories", new { name = "Cà phê" });
        await PublishedProductAsync(category.Id(), "Bạc xỉu", _b1);

        await _admin.Patch($"{Menu}/categories/{category.Id()}/status", new { status = "Hidden" }).ShouldBe(200);
        (await PublicMenuAsync(_b1)).GetProperty("categories").GetArrayLength().Should().Be(0);

        await _admin.Patch($"{Menu}/categories/{category.Id()}/status", new { status = "Active" }).ShouldBe(200);
        Products(await PublicMenuAsync(_b1)).Should().HaveCount(1);
        await _admin.Patch($"{Menu}/categories/{category.Id()}/status", new { }).ShouldBe(400);
    }

    [Fact]
    public async Task Branch_settings_can_be_listed_by_the_brand_and_a_manager_sees_only_their_branch()
    {
        var category = await _admin.PostOk($"{Menu}/categories", new { name = "Cà phê" });
        var product = await PublishedProductAsync(category.Id(), "Bạc xỉu", _b1, _b2);

        (await _admin.GetFromJsonAsync<JsonElement>($"{Menu}/products/{product.Id()}/branches")).GetArrayLength().Should().Be(2);
        var mine = await _f.As(AicosRoles.BranchManager, _b1).GetFromJsonAsync<JsonElement>($"{Menu}/products/{product.Id()}/branches");
        mine.GetArrayLength().Should().Be(1);
        mine[0].GetProperty("branchId").GetGuid().Should().Be(_b1);
        await _f.As(AicosRoles.Cashier, _b1).GetAsync($"{Menu}/products/{product.Id()}/branches").ShouldBe(403);
    }

    // ------------------------------------------------------------------ sold out (UC-24, BR-16)
    [Fact]
    public async Task A_branch_manager_marks_a_product_sold_out_for_their_branch_only()
    {
        var category = await _admin.PostOk($"{Menu}/categories", new { name = "Cà phê" });
        var product = await PublishedProductAsync(category.Id(), "Bạc xỉu", _b1, _b2);

        var response = await _f.As(AicosRoles.BranchManager, _b1).Patch($"{Menu}/branches/{_b1}/products/{product.Id()}/availability", new { isAvailable = false });
        (await response.Body()).GetProperty("isAvailable").GetBoolean().Should().BeFalse();

        Products(await PublicMenuAsync(_b1)).Should().BeEmpty();                  // customers cannot order it any more
        Products(await PublicMenuAsync(_b2)).Should().HaveCount(1);              // another branch is not affected
        var staffView = Products(await _f.As(AicosRoles.Cashier, _b1).GetFromJsonAsync<JsonElement>($"{Menu}/branches/{_b1}/menu")).Single();
        staffView.GetProperty("isAvailable").GetBoolean().Should().BeFalse();    // staff still see it, marked sold out

        await _f.As(AicosRoles.BranchManager, _b1).Patch($"{Menu}/branches/{_b1}/products/{product.Id()}/availability", new { isAvailable = true }).ShouldBe(200);
        Products(await PublicMenuAsync(_b1)).Should().HaveCount(1);
    }

    [Fact]
    public async Task Baristas_and_the_brand_admin_can_switch_availability_but_cashiers_and_other_branches_cannot()
    {
        var category = await _admin.PostOk($"{Menu}/categories", new { name = "Cà phê" });
        var product = await PublishedProductAsync(category.Id(), "Bạc xỉu", _b1);
        var url = $"{Menu}/branches/{_b1}/products/{product.Id()}/availability";

        await _f.As(AicosRoles.Barista, _b1).Patch(url, new { isAvailable = false }).ShouldBe(200);
        await _admin.Patch(url, new { isAvailable = true }).ShouldBe(200);
        await _f.As(AicosRoles.Cashier, _b1).Patch(url, new { isAvailable = false }).ShouldBe(403);
        await _f.As(AicosRoles.Customer).Patch(url, new { isAvailable = false }).ShouldBe(403);
        await _f.As(AicosRoles.BranchManager, _b2).Patch(url, new { isAvailable = false }).ShouldBe(403, "branch.forbidden");
        await _f.As(AicosRoles.Barista, _b1, tenantId: _otherTenant).Patch(url, new { isAvailable = false }).ShouldBe(404);          // same branch id, other brand
        await _f.CreateClient().PatchAsJsonAsync(url, new { isAvailable = false }).ShouldBe(401);
    }

    [Fact]
    public async Task Availability_rules_a_product_must_be_on_the_menu_and_a_value_is_required()
    {
        var category = await _admin.PostOk($"{Menu}/categories", new { name = "Cà phê" });
        var product = await _admin.PostOk($"{Menu}/products", Coffee(category.Id(), "Bạc xỉu"));
        var manager = _f.As(AicosRoles.BranchManager, _b1);

        await manager.Patch($"{Menu}/branches/{_b1}/products/{product.Id()}/availability", new { isAvailable = false }).ShouldBe(422, "product.not_published");
        await _admin.Put($"{Menu}/products/{product.Id()}/branches/{_b1}", new { isPublished = true }).ShouldBe(200);
        await manager.Patch($"{Menu}/branches/{_b1}/products/{product.Id()}/availability", new { }).ShouldBe(400);
        await manager.Patch($"{Menu}/branches/{_b1}/products/{Guid.NewGuid()}/availability", new { isAvailable = false }).ShouldBe(404);
        await _admin.Patch($"{Menu}/branches/{_otherBranch}/products/{product.Id()}/availability", new { isAvailable = false }).ShouldBe(400);
    }

    [Fact]
    public async Task Publishing_again_after_unpublishing_makes_the_product_available_again()
    {
        var category = await _admin.PostOk($"{Menu}/categories", new { name = "Cà phê" });
        var product = await PublishedProductAsync(category.Id(), "Bạc xỉu", _b1);
        await _f.As(AicosRoles.BranchManager, _b1).Patch($"{Menu}/branches/{_b1}/products/{product.Id()}/availability", new { isAvailable = false }).ShouldBe(200);
        await _admin.Put($"{Menu}/products/{product.Id()}/branches/{_b1}", new { isPublished = false }).ShouldBe(200);

        await _admin.Put($"{Menu}/products/{product.Id()}/branches/{_b1}", new { isPublished = true }).ShouldBe(200);

        Products(await PublicMenuAsync(_b1)).Should().HaveCount(1);
    }

    // ------------------------------------------------------------------ staff view of a branch menu
    [Fact]
    public async Task The_staff_menu_of_a_branch_is_for_that_branchs_staff_and_the_brand_admin()
    {
        var category = await _admin.PostOk($"{Menu}/categories", new { name = "Cà phê" });
        await PublishedProductAsync(category.Id(), "Bạc xỉu", _b1);

        (await _f.As(AicosRoles.Barista, _b1).GetFromJsonAsync<JsonElement>($"{Menu}/branches/{_b1}/menu")).GetProperty("categories").GetArrayLength().Should().Be(1);
        (await _admin.GetFromJsonAsync<JsonElement>($"{Menu}/branches/{_b1}/menu")).GetProperty("categories").GetArrayLength().Should().Be(1);
        await _f.As(AicosRoles.Cashier, _b2).GetAsync($"{Menu}/branches/{_b1}/menu").ShouldBe(403, "branch.forbidden");
        await _f.As(AicosRoles.Customer).GetAsync($"{Menu}/branches/{_b1}/menu").ShouldBe(403);
        await _otherAdmin.GetAsync($"{Menu}/branches/{_b1}/menu").ShouldBe(400);   // another brand's branch does not exist for them
    }

    [Fact]
    public async Task The_public_menu_needs_no_login_and_an_unknown_branch_is_simply_empty()
    {
        var anonymous = _f.CreateClient();

        var empty = await anonymous.GetFromJsonAsync<JsonElement>($"{Menu}/public/branches/{Guid.NewGuid()}/menu");

        empty.GetProperty("categories").GetArrayLength().Should().Be(0);
    }

    // ------------------------------------------------------------------ cache and change events (NFR-25)
    [Fact]
    public async Task The_published_menu_is_cached_and_every_change_invalidates_it()
    {
        var category = await _admin.PostOk($"{Menu}/categories", new { name = "Cà phê" });
        var product = await PublishedProductAsync(category.Id(), "Bạc xỉu", _b1);
        var anonymous = _f.CreateClient();
        string Url() => $"{Menu}/public/branches/{_b1}/menu";

        await anonymous.GetStringAsync(Url());
        var hitsBefore = _f.Cache.Hits;
        await anonymous.GetStringAsync(Url());
        _f.Cache.Hits.Should().Be(hitsBefore + 2, "the second read finds the brand of the branch and the menu itself in the cache");

        await _f.As(AicosRoles.BranchManager, _b1).Patch($"{Menu}/branches/{_b1}/products/{product.Id()}/availability", new { isAvailable = false }).ShouldBe(200);
        Products(await anonymous.GetFromJsonAsync<JsonElement>(Url())).Should().BeEmpty();                                  // sold out shows at once

        await _f.As(AicosRoles.BranchManager, _b1).Patch($"{Menu}/branches/{_b1}/products/{product.Id()}/availability", new { isAvailable = true }).ShouldBe(200);
        await _admin.Put($"{Menu}/products/{product.Id()}/branches/{_b1}", new { isPublished = true, priceOverride = 50_000 }).ShouldBe(200);
        Products(await anonymous.GetFromJsonAsync<JsonElement>(Url())).Single().GetProperty("price").GetDecimal().Should().Be(50_000);

        await _admin.Put($"{Menu}/products/{product.Id()}", Coffee(category.Id(), "Bạc xỉu mới")).ShouldBe(200);
        Products(await anonymous.GetFromJsonAsync<JsonElement>(Url())).Single().GetProperty("name").GetString().Should().Be("Bạc xỉu mới");

        await _admin.Patch($"{Menu}/categories/{category.Id()}/status", new { status = "Hidden" }).ShouldBe(200);
        (await anonymous.GetFromJsonAsync<JsonElement>(Url())).GetProperty("categories").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Every_cache_key_starts_with_the_brand_it_belongs_to()
    {
        var category = await _admin.PostOk($"{Menu}/categories", new { name = "Cà phê" });
        var product = await PublishedProductAsync(category.Id(), "Bạc xỉu", _b1);
        await _f.CreateClient().GetStringAsync($"{Menu}/public/branches/{_b1}/menu");                                            // anonymous: the brand is looked up from the branch
        await _f.As(AicosRoles.BranchManager, _b1).GetAsync($"{Menu}/branches/{_b1}/menu").ShouldBe(200);                          // staff view
        await _f.As(AicosRoles.BranchManager, _b1).Patch($"{Menu}/branches/{_b1}/products/{product.Id()}/availability", new { isAvailable = false }).ShouldBe(200);

        var menuKeys = _f.Cache.Keys.Where(k => k.Contains("menu:")).ToList();

        menuKeys.Should().NotBeEmpty().And.OnlyContain(k => k.StartsWith($"t:{_tenant}:menu:"));
        menuKeys.Should().Contain(k => k.Contains(":menu:public:")).And.Contain(k => k.Contains(":menu:staff:")).And.Contain(k => k.Contains(":menu:version:"));
        _f.Cache.Keys.Should().OnlyContain(k => k.StartsWith("t:") || k.StartsWith("branch-tenant:"));
    }

    [Fact]
    public async Task Changes_tell_the_other_services_with_a_menu_changed_event()
    {
        var category = await _admin.PostOk($"{Menu}/categories", new { name = "Cà phê" });
        var product = await _admin.PostOk($"{Menu}/products", Coffee(category.Id(), "Bạc xỉu"));
        _f.Events.Published.Clear();

        await _admin.Put($"{Menu}/products/{product.Id()}/branches/{_b1}", new { isPublished = true }).ShouldBe(200);
        await _f.As(AicosRoles.BranchManager, _b1).Patch($"{Menu}/branches/{_b1}/products/{product.Id()}/availability", new { isAvailable = false }).ShouldBe(200);
        await _admin.Put($"{Menu}/products/{product.Id()}", Coffee(category.Id(), "Bạc xỉu 2")).ShouldBe(200);

        var events = _f.Events.Published.OfType<MenuChangedEvent>().ToList();
        events.Select(e => e.Reason).Should().Equal("branch_product.changed", "product.sold_out", "product.updated");
        events.Should().OnlyContain(e => e.TenantId == _tenant);
        events[0].BranchId.Should().Be(_b1);
        events[1].BranchId.Should().Be(_b1);
        events[2].BranchId.Should().BeNull(); // an edit of the product reaches every branch that sells it
    }

    [Fact]
    public async Task A_failed_change_publishes_no_event()
    {
        var category = await _admin.PostOk($"{Menu}/categories", new { name = "Cà phê" });
        var product = await _admin.PostOk($"{Menu}/products", Coffee(category.Id(), "Bạc xỉu"));
        _f.Events.Published.Clear();

        await _admin.Put($"{Menu}/products/{product.Id()}/branches/{_otherBranch}", new { isPublished = true }).ShouldBe(400);
        await _f.As(AicosRoles.Cashier, _b1).Patch($"{Menu}/branches/{_b1}/products/{product.Id()}/availability", new { isAvailable = false }).ShouldBe(403);

        _f.Events.Published.OfType<MenuChangedEvent>().Should().BeEmpty();
    }

    // ------------------------------------------------------------------ helpers
    private static object Coffee(Guid categoryId, string name, decimal price = 35_000) => new
    {
        categoryId, name, description = "Cà phê", basePrice = price, tags = new[] { "coffee" },
        optionGroups = new object[]
        {
            new { name = "Size", kind = "Size", selectionType = "Single", isRequired = true,
                  options = new object[] { new { name = "S", priceDelta = 0, isDefault = true }, new { name = "M", priceDelta = 5_000 }, new { name = "L", priceDelta = 10_000 } } },
            new { name = "Sugar", kind = "Sugar", selectionType = "Single", isRequired = true,
                  options = new object[] { new { name = "0%", priceDelta = 0 }, new { name = "50%", priceDelta = 0, isDefault = true }, new { name = "100%", priceDelta = 0 } } },
            new { name = "Ice", kind = "Ice", selectionType = "Single", isRequired = true,
                  options = new object[] { new { name = "No ice", priceDelta = 0 }, new { name = "Less", priceDelta = 0, isDefault = true } } },
            new { name = "Topping", kind = "Topping", selectionType = "Multiple", isRequired = false, maxSelections = 2,
                  options = new object[] { new { name = "Pearl", priceDelta = 8_000 }, new { name = "Pudding", priceDelta = 8_000 }, new { name = "Cream", priceDelta = 6_000 } } },
        },
    };

    private async Task<JsonElement> PublishedProductAsync(Guid categoryId, string name, params Guid[] branches)
    {
        var product = await _admin.PostOk($"{Menu}/products", Coffee(categoryId, name));
        foreach (var branch in branches)
        {
            await _admin.Put($"{Menu}/products/{product.Id()}/branches/{branch}", new { isPublished = true }).ShouldBe(200);
        }

        return product;
    }

    private Task<JsonElement> PublicMenuAsync(Guid branchId) => _f.CreateClient().GetFromJsonAsync<JsonElement>($"{Menu}/public/branches/{branchId}/menu");

    private static List<JsonElement> Products(JsonElement menu) =>
        menu.GetProperty("categories").EnumerateArray().SelectMany(c => c.GetProperty("products").EnumerateArray()).ToList();
}
