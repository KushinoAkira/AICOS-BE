using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Persistence;
using AICOS.BuildingBlocks.Security;
using AICOS.MenuCatalog.Api.Domain;
using AICOS.MenuCatalog.Api.Infrastructure;
using AICOS.MenuCatalog.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.MenuCatalog.Api.Features.Products;

/// <summary>
/// UC-14 Manage Menu - products with option groups. Routes: /api/v1/menu/products.
/// Read: every staff role of the brand. Write: BrandAdmin only (BR-12). Delete is soft (orders keep a name snapshot).
/// Which branches sell a product, at what price, and whether it is sold out are in <c>BranchMenuEndpoints</c>.
/// </summary>
public static class ProductEndpoints
{
    public static void MapProductEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/products").WithTags("Products");

        group.MapGet("/", ListAsync).RequireAuthorization(AicosPolicies.Staff);
        group.MapGet("/{id:guid}", GetAsync).RequireAuthorization(AicosPolicies.Staff).WithName("GetProduct");
        group.MapPost("/", CreateAsync).RequireAuthorization(AicosPolicies.AdminOnly);
        group.MapPut("/{id:guid}", UpdateAsync).RequireAuthorization(AicosPolicies.AdminOnly);
        group.MapDelete("/{id:guid}", DeleteAsync).RequireAuthorization(AicosPolicies.AdminOnly);
    }

    private static async Task<PagedResponse<ProductResponse>> ListAsync(
        MenuCatalogDbContext db,
        ICurrentUser user,
        Guid? categoryId,
        string? search,
        int page = 1,
        int pageSize = 20,
        CancellationToken ct = default)
    {
        var tenantId = TenantScope.Require(user);
        var query = db.Products.AsNoTracking().Include(p => p.OptionGroups).ThenInclude(g => g.Options).AsSplitQuery()
            .Where(p => p.TenantId == tenantId);

        if (categoryId is not null)
        {
            query = query.Where(p => p.CategoryId == categoryId);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(p => p.Name.ToLower().Contains(term) || (p.Description != null && p.Description.ToLower().Contains(term)));
        }

        return await query.OrderBy(p => p.Name).ToPagedAsync(page, pageSize, ProductResponse.From, ct);
    }

    private static async Task<ProductResponse> GetAsync(Guid id, MenuCatalogDbContext db, ICurrentUser user, CancellationToken ct) =>
        ProductResponse.From(await FindAsync(db, user, id, ct));

    private static async Task<IResult> CreateAsync(
        ProductRequest request, MenuCatalogDbContext db, ICurrentUser user, MenuChangeNotifier notifier, CancellationToken ct)
    {
        ProductValidator.EnsureValid(request);
        var tenantId = TenantScope.Require(user);
        await EnsureCategoryInTenantAsync(db, request.CategoryId!.Value, tenantId, ct);
        await EnsureNameUniqueAsync(db, tenantId, request.Name!, null, ct);

        var product = new Product(tenantId, request.CategoryId!.Value, request.Name!, request.Description, request.ImageUrl, request.BasePrice!.Value, request.Tags ?? []);
        product.ReplaceOptionGroups(ProductValidator.ToEntities(request.OptionGroups));
        db.Products.Add(product);
        await db.SaveChangesAsync(ct);

        // A new product is on no branch menu until it is published, so there is nothing to refresh yet.
        return Results.CreatedAtRoute("GetProduct", new { id = product.Id }, ProductResponse.From(product));
    }

    private static async Task<ProductResponse> UpdateAsync(
        Guid id, ProductRequest request, MenuCatalogDbContext db, ICurrentUser user, MenuChangeNotifier notifier, CancellationToken ct)
    {
        ProductValidator.EnsureValid(request);
        var product = await FindAsync(db, user, id, ct);
        await EnsureCategoryInTenantAsync(db, request.CategoryId!.Value, product.TenantId, ct);
        await EnsureNameUniqueAsync(db, product.TenantId, request.Name!, id, ct);

        product.Update(request.CategoryId!.Value, request.Name!, request.Description, request.ImageUrl, request.BasePrice!.Value, request.Tags ?? []);
        product.ReplaceOptionGroups(ProductValidator.ToEntities(request.OptionGroups));
        await db.SaveChangesAsync(ct);

        await notifier.TenantChangedAsync(product.TenantId, "product.updated", ct);
        return ProductResponse.From(product);
    }

    private static async Task<IResult> DeleteAsync(
        Guid id, MenuCatalogDbContext db, ICurrentUser user, TimeProvider clock, MenuChangeNotifier notifier, CancellationToken ct)
    {
        var product = await FindAsync(db, user, id, ct);
        product.MarkDeleted(clock.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct);

        await notifier.TenantChangedAsync(product.TenantId, "product.deleted", ct);
        return Results.NoContent();
    }

    private static async Task EnsureCategoryInTenantAsync(MenuCatalogDbContext db, Guid categoryId, Guid tenantId, CancellationToken ct)
    {
        if (!await db.Categories.AnyAsync(c => c.Id == categoryId && c.TenantId == tenantId, ct))
        {
            throw new ValidationFailedException("CategoryId", "Category does not exist in this brand.");
        }
    }

    private static async Task EnsureNameUniqueAsync(MenuCatalogDbContext db, Guid tenantId, string name, Guid? excludeId, CancellationToken ct)
    {
        var normalized = name.Trim().ToLower();
        if (await db.Products.AnyAsync(p => p.TenantId == tenantId && p.Name.ToLower() == normalized && p.Id != excludeId, ct))
        {
            throw new ConflictException($"Product '{name.Trim()}' already exists in this brand.", "product.duplicate_name");
        }
    }

    /// <summary>A product of another tenant is reported as not found: its existence is not revealed.</summary>
    internal static async Task<Product> FindAsync(MenuCatalogDbContext db, ICurrentUser user, Guid id, CancellationToken ct)
    {
        var tenantId = TenantScope.Require(user);
        return await db.Products.Include(p => p.OptionGroups).ThenInclude(g => g.Options).AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Id == id && p.TenantId == tenantId, ct) ?? throw new NotFoundException("Product", id);
    }
}
