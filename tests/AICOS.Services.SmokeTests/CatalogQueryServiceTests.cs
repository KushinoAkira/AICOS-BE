using System.Net;
using AICOS.BuildingBlocks.Security;
using AICOS.MenuCatalog.Api;
using AICOS.MenuCatalog.Api.Domain;
using AICOS.MenuCatalog.Api.Features.Catalog;
using AICOS.MenuCatalog.Api.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace AICOS.Services.SmokeTests;

public sealed class CatalogQueryServiceTests
{
    [Fact]
    public void Catalog_relationships_do_not_cascade_delete()
    {
        using var context = CreateContext();
        context.Model.GetEntityTypes()
            .SelectMany(entity => entity.GetForeignKeys())
            .Should().NotBeEmpty()
            .And.OnlyContain(foreignKey => foreignKey.DeleteBehavior == DeleteBehavior.NoAction);
    }

    [Fact]
    public async Task Catalog_endpoint_requires_an_access_token()
    {
        await using var factory = new WebApplicationFactory<MenuCatalogService>();
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/v1/menu/catalog");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Catalog_returns_only_products_available_in_the_token_branch()
    {
        await using var context = CreateContext();
        var branchId = Guid.NewGuid();
        var otherBranchId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var category = new Category(Guid.NewGuid(), tenantId, "Cà phê");
        var visible = new Product(Guid.NewGuid(), tenantId, category.Id, "Cà phê sữa");
        var hidden = new Product(Guid.NewGuid(), tenantId, category.Id, "Cà phê đen");
        var variant = new ProductVariant(Guid.NewGuid(), visible.Id, "M", 35000m);
        var group = new ProductOptionGroup(Guid.NewGuid(), visible.Id, "Đường");
        var option = new ProductOption(Guid.NewGuid(), group.Id, "Ít đường");
        context.AddRange(
            category,
            visible,
            hidden,
            variant,
            group,
            option,
            new BranchProduct(Guid.NewGuid(), branchId, visible.Id),
            new BranchProduct(Guid.NewGuid(), otherBranchId, hidden.Id));
        await context.SaveChangesAsync();

        var response = await new CatalogQueryService(context).GetAsync(
            new TestCurrentUser(branchId),
            null,
            null,
            CancellationToken.None);

        response.Categories.Should().ContainSingle();
        var product = response.Categories[0].Products.Should().ContainSingle().Subject;
        product.Id.Should().Be(visible.Id);
        product.Variants.Should().ContainSingle(item => item.Price == 35000m);
        product.OptionGroups.Should().ContainSingle()
            .Which.Options.Should().ContainSingle(item => item.Name == "Ít đường");
    }

    private static MenuCatalogDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MenuCatalogDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new MenuCatalogDbContext(options);
    }

    private sealed class TestCurrentUser(Guid branchId) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid? UserId { get; } = Guid.NewGuid();
        public string? Name => "Catalog Test Staff";
        public string? Role => AicosRoles.Cashier;
        public Guid? BranchId => branchId;
        public bool IsInRole(string role) => role == AicosRoles.Cashier;
    }
}
