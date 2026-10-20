using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Persistence;
using AICOS.BuildingBlocks.Security;
using AICOS.MenuCatalog.Api.Domain;
using AICOS.MenuCatalog.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.MenuCatalog.Api.Features.Products;

/// <summary>
/// UC-06 Manage Menu - products with option groups. Routes: /api/v1/menu/products.
/// Read: all staff (the POS needs it). Write: BrandAdmin/Manager. Delete is soft (orders keep a name snapshot).
/// </summary>
public static class ProductEndpoints
{
    public static void MapProductEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/products").WithTags("Products");

        group.MapGet("/", ListAsync).RequireAuthorization(AicosPolicies.Staff);
        group.MapGet("/{id:guid}", GetAsync).RequireAuthorization(AicosPolicies.Staff).WithName("GetProduct");
        group.MapPost("/", CreateAsync).RequireAuthorization(AicosPolicies.Management);
        group.MapPut("/{id:guid}", UpdateAsync).RequireAuthorization(AicosPolicies.Management);
        group.MapPatch("/{id:guid}/availability", ChangeAvailabilityAsync).RequireAuthorization(AicosPolicies.Staff);
        group.MapDelete("/{id:guid}", DeleteAsync).RequireAuthorization(AicosPolicies.Management);
    }

    private static async Task<PagedResponse<ProductResponse>> ListAsync(
        MenuCatalogDbContext db,
        ICurrentUser user,
        Guid? branchId,
        Guid? categoryId,
        bool? isAvailable,
        string? search,
        int page = 1,
        int pageSize = 20,
        CancellationToken ct = default)
    {
        var branch = BranchAccess.ResolveFilter(user, branchId);
        var query = db.Products.AsNoTracking().Include(p => p.OptionGroups).ThenInclude(g => g.Options).AsSplitQuery();

        if (branch is not null)
        {
            query = query.Where(p => p.BranchId == branch);
        }

        if (categoryId is not null)
        {
            query = query.Where(p => p.CategoryId == categoryId);
        }

        if (isAvailable is not null)
        {
            query = query.Where(p => p.IsAvailable == isAvailable);
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

    private static async Task<IResult> CreateAsync(ProductRequest request, MenuCatalogDbContext db, ICurrentUser user, CancellationToken ct)
    {
        ProductValidator.EnsureValid(request);
        var branchId = BranchAccess.Resolve(user, request.BranchId);
        await EnsureCategoryInBranchAsync(db, request.CategoryId!.Value, branchId, ct);
        await EnsureNameUniqueAsync(db, branchId, request.Name!, null, ct);

        var product = new Product(branchId, request.CategoryId!.Value, request.Name!, request.Description, request.ImageUrl, request.BasePrice!.Value, request.Tags ?? []);
        product.ReplaceOptionGroups(ProductValidator.ToEntities(request.OptionGroups));
        db.Products.Add(product);
        await db.SaveChangesAsync(ct);

        return Results.CreatedAtRoute("GetProduct", new { id = product.Id }, ProductResponse.From(product));
    }

    private static async Task<ProductResponse> UpdateAsync(
        Guid id, ProductRequest request, MenuCatalogDbContext db, ICurrentUser user, CancellationToken ct)
    {
        ProductValidator.EnsureValid(request);
        var product = await FindAsync(db, user, id, ct);
        await EnsureCategoryInBranchAsync(db, request.CategoryId!.Value, product.BranchId, ct);
        await EnsureNameUniqueAsync(db, product.BranchId, request.Name!, id, ct);

        product.Update(request.CategoryId!.Value, request.Name!, request.Description, request.ImageUrl, request.BasePrice!.Value, request.Tags ?? []);
        product.ReplaceOptionGroups(ProductValidator.ToEntities(request.OptionGroups));
        await db.SaveChangesAsync(ct);
        return ProductResponse.From(product);
    }

    /// <summary>Cashiers may also mark an item sold out / back in stock during the day.</summary>
    private static async Task<ProductResponse> ChangeAvailabilityAsync(
        Guid id, ChangeAvailabilityRequest request, MenuCatalogDbContext db, ICurrentUser user, CancellationToken ct)
    {
        if (request.IsAvailable is null)
        {
            throw new ValidationFailedException("IsAvailable", "IsAvailable is required (true or false).");
        }

        var product = await FindAsync(db, user, id, ct);
        product.SetAvailability(request.IsAvailable.Value);
        await db.SaveChangesAsync(ct);
        return ProductResponse.From(product);
    }

    private static async Task<IResult> DeleteAsync(Guid id, MenuCatalogDbContext db, ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        var product = await FindAsync(db, user, id, ct);
        product.MarkDeleted(clock.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task EnsureCategoryInBranchAsync(MenuCatalogDbContext db, Guid categoryId, Guid branchId, CancellationToken ct)
    {
        var category = await db.Categories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == categoryId, ct);
        if (category is null || category.BranchId != branchId)
        {
            throw new ValidationFailedException("CategoryId", "Category does not exist in this branch.");
        }
    }

    private static async Task EnsureNameUniqueAsync(MenuCatalogDbContext db, Guid branchId, string name, Guid? excludeId, CancellationToken ct)
    {
        var normalized = name.Trim().ToLower();
        if (await db.Products.AnyAsync(p => p.BranchId == branchId && p.Name.ToLower() == normalized && p.Id != excludeId, ct))
        {
            throw new ConflictException($"Product '{name.Trim()}' already exists in this branch.", "product.duplicate_name");
        }
    }

    private static async Task<Product> FindAsync(MenuCatalogDbContext db, ICurrentUser user, Guid id, CancellationToken ct)
    {
        var product = await db.Products.Include(p => p.OptionGroups).ThenInclude(g => g.Options).AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("Product", id);
        BranchAccess.EnsureCanAccess(user, product.BranchId);
        return product;
    }
}
