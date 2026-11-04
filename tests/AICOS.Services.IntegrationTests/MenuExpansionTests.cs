using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.MenuCatalog.Api;
using AICOS.MenuCatalog.Api.Domain;
using AICOS.MenuCatalog.Api.Features.Products;
using AICOS.MenuCatalog.Api.Persistence;

namespace AICOS.Services.IntegrationTests;

public sealed class MenuExpansionTests
{
    [Fact]
    public async Task Expanding_options_keeps_ids_dependencies_and_supports_more_than_two_toppings()
    {
        using var factory = new ServiceFactory<MenuCatalogService, MenuCatalogDbContext>();
        using var admin = factory.As(AicosRoles.BrandAdmin);
        var category = await (await admin.PostAsJsonAsync("/api/v1/menu/categories", new { name = "Expansion", sortOrder = 0 }))
            .Content.ReadFromJsonAsync<JsonElement>();
        OptionGroupRequest Size() => new("Size", OptionKind.Size, SelectionType.Single, true, null, 0,
            [new("M", 0, true), new("L", 8000, false)]);
        OptionGroupRequest Toppings(bool expanded) => new("Topping", OptionKind.Topping, SelectionType.Multiple, false,
            expanded ? null : 2, 1, expanded
                ? [new("Foam", 10000, false, "Size", "L"), new("Pearl", 7000, false), new("Pudding", 8000, false), new("Jelly", 7000, false)]
                : [new("Foam", 10000, false, "Size", "L"), new("Pearl", 7000, false)]);
        ProductRequest Body(bool expanded) => new(category.GetProperty("id").GetGuid(), "Milk tea", null, null,
            35000, ["local-fixture"], [Size(), Toppings(expanded)]);
        var created = await (await admin.PostAsJsonAsync("/api/v1/menu/products", Body(false))).Read<ProductResponse>();
        var originalGroup = created.OptionGroups[1];
        var originalFoam = originalGroup.Options[0];
        foreach (var repeat in Enumerable.Range(0, 2))
        {
            var response = await admin.PutAsJsonAsync($"/api/v1/menu/products/{created.Id}", Body(true));
            response.EnsureSuccessStatusCode();
            var updated = await response.Read<ProductResponse>();
            updated.OptionGroups[0].Id.Should().Be(created.OptionGroups[0].Id);
            var topping = updated.OptionGroups[1];
            topping.Id.Should().Be(originalGroup.Id);
            topping.MaxSelections.Should().BeNull();
            topping.Options.Should().HaveCount(4);
            var foam = topping.Options.Single(o => o.Name == "Foam");
            foam.Id.Should().Be(originalFoam.Id);
            foam.RequiresOptionId.Should().Be(created.OptionGroups[0].Options.Single(o => o.Name == "L").Id);
        }
    }
}
