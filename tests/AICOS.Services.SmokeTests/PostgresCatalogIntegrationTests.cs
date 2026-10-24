using System.Net.Http.Headers;
using System.Net.Http.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.IdentityOrganization.Api.Domain;
using AICOS.IdentityOrganization.Api.Persistence;
using AICOS.MenuCatalog.Api;
using AICOS.MenuCatalog.Api.Domain;
using AICOS.MenuCatalog.Api.Features.Catalog;
using AICOS.MenuCatalog.Api.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AICOS.Services.SmokeTests;

public sealed class PostgresCatalogIntegrationTests
{
    [Fact]
    public async Task Catalog_reads_branch_products_from_real_postgres_when_enabled()
    {
        var connectionString = Environment.GetEnvironmentVariable("AICOS_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("AICOS_REQUIRE_POSTGRES_INTEGRATION"), "true", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("AICOS_TEST_POSTGRES is required for the PostgreSQL integration test.");
            }
            return;
        }

        var ids = TestIds.Create();
        try
        {
            await SeedAsync(connectionString, ids);
            const string key = "catalog-integration-signing-key-0123456789-abcdef";
            await using var factory = new WebApplicationFactory<MenuCatalogService>()
                .WithWebHostBuilder(builder => builder
                    .UseSetting("ConnectionStrings:Default", connectionString)
                    .UseSetting("Database:MigrateOnStartup", "false")
                    .UseSetting("Jwt:Issuer", "aicos-catalog-integration")
                    .UseSetting("Jwt:Audience", "aicos-catalog-clients")
                    .UseSetting("Jwt:SigningKey", key));
            using var client = factory.CreateClient();
            var tokenService = new JwtTokenService(Options.Create(new JwtOptions
            {
                Issuer = "aicos-catalog-integration",
                Audience = "aicos-catalog-clients",
                SigningKey = key,
                ExpiryMinutes = 30,
            }), TimeProvider.System);
            var (token, _) = tokenService.CreateAccessToken(Guid.NewGuid(), "Catalog Staff", AicosRoles.Cashier, ids.BranchId);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var catalog = await client.GetFromJsonAsync<CatalogResponse>(
                $"/api/v1/menu/catalog?categoryId={ids.CategoryId}&search={Uri.EscapeDataString("SỮA")}");

            catalog.Should().NotBeNull();
            catalog!.BranchId.Should().Be(ids.BranchId);
            var product = catalog.Categories.Should().ContainSingle().Subject.Products.Should().ContainSingle().Subject;
            product.Id.Should().Be(ids.ProductId);
            product.Variants.Should().ContainSingle(item => item.Id == ids.VariantId && item.Price == 39000m);
            product.OptionGroups.Should().ContainSingle().Which.Options.Should().ContainSingle(item => item.Id == ids.OptionId);
        }
        finally
        {
            await CleanupAsync(connectionString, ids);
        }
    }

    private static async Task SeedAsync(string connectionString, TestIds ids)
    {
        await using (var identity = CreateIdentityContext(connectionString))
        {
            identity.AddRange(
                new Tenant(ids.TenantId, "Catalog Integration Tenant", ids.TenantCode),
                new Branch(ids.BranchId, ids.TenantId, ids.BranchCode, "Catalog Integration Branch", IdentityStatuses.Active));
            await identity.SaveChangesAsync();
        }

        await using var catalog = CreateCatalogContext(connectionString);
        catalog.AddRange(
            new Category(ids.CategoryId, ids.TenantId, "Cà phê"),
            new Product(ids.ProductId, ids.TenantId, ids.CategoryId, "Cà phê sữa"),
            new ProductVariant(ids.VariantId, ids.ProductId, "M", 39000m),
            new ProductOptionGroup(ids.GroupId, ids.ProductId, "Đường"),
            new ProductOption(ids.OptionId, ids.GroupId, "Ít đường"),
            new BranchProduct(ids.BranchProductId, ids.BranchId, ids.ProductId));
        await catalog.SaveChangesAsync();
    }

    private static async Task CleanupAsync(string connectionString, TestIds ids)
    {
        await using (var catalog = CreateCatalogContext(connectionString))
        {
            await catalog.ProductOptions.Where(x => x.Id == ids.OptionId).ExecuteDeleteAsync();
            await catalog.ProductOptionGroups.Where(x => x.Id == ids.GroupId).ExecuteDeleteAsync();
            await catalog.ProductVariants.Where(x => x.Id == ids.VariantId).ExecuteDeleteAsync();
            await catalog.BranchProducts.Where(x => x.Id == ids.BranchProductId).ExecuteDeleteAsync();
            await catalog.Products.Where(x => x.Id == ids.ProductId).ExecuteDeleteAsync();
            await catalog.Categories.Where(x => x.Id == ids.CategoryId).ExecuteDeleteAsync();
        }
        await using var identity = CreateIdentityContext(connectionString);
        await identity.Branches.Where(x => x.Id == ids.BranchId).ExecuteDeleteAsync();
        await identity.Tenants.Where(x => x.Id == ids.TenantId).ExecuteDeleteAsync();
    }

    private static MenuCatalogDbContext CreateCatalogContext(string connectionString) =>
        new(new DbContextOptionsBuilder<MenuCatalogDbContext>().UseNpgsql(connectionString).UseSnakeCaseNamingConvention().Options);

    private static IdentityOrganizationDbContext CreateIdentityContext(string connectionString) =>
        new(new DbContextOptionsBuilder<IdentityOrganizationDbContext>().UseNpgsql(connectionString).UseSnakeCaseNamingConvention().Options);

    private sealed record TestIds(Guid TenantId, Guid BranchId, Guid CategoryId, Guid ProductId, Guid VariantId, Guid GroupId, Guid OptionId, Guid BranchProductId, string TenantCode, string BranchCode)
    {
        public static TestIds Create()
        {
            var suffix = Guid.NewGuid().ToString("N")[..10];
            return new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), $"CA-{suffix}", $"CA-{suffix}");
        }
    }
}
