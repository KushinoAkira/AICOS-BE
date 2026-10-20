using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.BuildingBlocks.Validation;
using AICOS.MenuCatalog.Api.Domain;
using AICOS.MenuCatalog.Api.Infrastructure;
using AICOS.MenuCatalog.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.MenuCatalog.Api.Features.Categories;

public sealed record CategoryRequest(string? Name, int? SortOrder);

public sealed record ChangeCategoryStatusRequest(CategoryStatus? Status);

public sealed record CategoryResponse(Guid Id, string Name, int SortOrder, CategoryStatus Status, int ProductCount)
{
    public static CategoryResponse From(Category c, int productCount = 0) => new(c.Id, c.Name, c.SortOrder, c.Status, productCount);
}

/// <summary>
/// UC-14 Manage Menu - categories of the brand. Routes: /api/v1/menu/categories. Read: every staff role of the brand.
/// Write: BrandAdmin only (BR-12). A change reaches every branch menu that shows the category.
/// </summary>
public static class CategoryEndpoints
{
    public static void MapCategoryEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/categories").WithTags("Categories");

        group.MapGet("/", ListAsync).RequireAuthorization(AicosPolicies.Staff);
        group.MapGet("/{id:guid}", GetAsync).RequireAuthorization(AicosPolicies.Staff).WithName("GetCategory");
        group.MapPost("/", CreateAsync).RequireAuthorization(AicosPolicies.AdminOnly);
        group.MapPut("/{id:guid}", UpdateAsync).RequireAuthorization(AicosPolicies.AdminOnly);
        group.MapPatch("/{id:guid}/status", ChangeStatusAsync).RequireAuthorization(AicosPolicies.AdminOnly);
        group.MapDelete("/{id:guid}", DeleteAsync).RequireAuthorization(AicosPolicies.AdminOnly);
    }

    private static async Task<List<CategoryResponse>> ListAsync(MenuCatalogDbContext db, ICurrentUser user, CategoryStatus? status, CancellationToken ct)
    {
        var tenantId = TenantScope.Require(user);
        var query = db.Categories.AsNoTracking().Where(c => c.TenantId == tenantId);
        if (status is not null)
        {
            query = query.Where(c => c.Status == status);
        }

        var rows = await query
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .Select(c => new { Category = c, Count = db.Products.Count(p => p.CategoryId == c.Id) })
            .ToListAsync(ct);

        return rows.Select(r => CategoryResponse.From(r.Category, r.Count)).ToList();
    }

    private static async Task<CategoryResponse> GetAsync(Guid id, MenuCatalogDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var category = await FindAsync(db, user, id, ct);
        return CategoryResponse.From(category, await db.Products.CountAsync(p => p.CategoryId == id, ct));
    }

    private static async Task<IResult> CreateAsync(
        CategoryRequest request, MenuCatalogDbContext db, ICurrentUser user, MenuChangeNotifier notifier, CancellationToken ct)
    {
        Validate(request);
        var tenantId = TenantScope.Require(user);
        await EnsureNameUniqueAsync(db, tenantId, request.Name!, null, ct);

        var category = new Category(tenantId, request.Name!, request.SortOrder ?? 0);
        db.Categories.Add(category);
        await db.SaveChangesAsync(ct);

        await notifier.TenantChangedAsync(tenantId, "category.created", ct);
        return Results.CreatedAtRoute("GetCategory", new { id = category.Id }, CategoryResponse.From(category));
    }

    private static async Task<CategoryResponse> UpdateAsync(
        Guid id, CategoryRequest request, MenuCatalogDbContext db, ICurrentUser user, MenuChangeNotifier notifier, CancellationToken ct)
    {
        Validate(request);
        var category = await FindAsync(db, user, id, ct);
        await EnsureNameUniqueAsync(db, category.TenantId, request.Name!, id, ct);

        category.Update(request.Name!, request.SortOrder ?? category.SortOrder);
        await db.SaveChangesAsync(ct);

        await notifier.TenantChangedAsync(category.TenantId, "category.updated", ct);
        return CategoryResponse.From(category, await db.Products.CountAsync(p => p.CategoryId == id, ct));
    }

    private static async Task<CategoryResponse> ChangeStatusAsync(
        Guid id, ChangeCategoryStatusRequest request, MenuCatalogDbContext db, ICurrentUser user, MenuChangeNotifier notifier, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        CommonRules.RequiredEnum(errors, "Status", request.Status, "Status");
        errors.ThrowIfAny();

        var category = await FindAsync(db, user, id, ct);
        category.ChangeStatus(request.Status!.Value);
        await db.SaveChangesAsync(ct);

        await notifier.TenantChangedAsync(category.TenantId, "category.status_changed", ct);
        return CategoryResponse.From(category, await db.Products.CountAsync(p => p.CategoryId == id, ct));
    }

    private static async Task<IResult> DeleteAsync(Guid id, MenuCatalogDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var category = await FindAsync(db, user, id, ct);
        if (await db.Products.AnyAsync(p => p.CategoryId == id, ct))
        {
            throw new ConflictException(
                "This category still contains products. Move or delete them first, or hide the category instead.",
                "category.has_products");
        }

        // Products deleted earlier are only soft-deleted; they go for good with their category.
        db.Products.RemoveRange(await db.Products.IgnoreQueryFilters().Where(p => p.CategoryId == id).ToListAsync(ct));
        db.Categories.Remove(category);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static void Validate(CategoryRequest request)
    {
        var errors = new ValidationErrors();
        CommonRules.Required(errors, "Name", request.Name, "Category name", Category.NameMaxLength);
        if (request.SortOrder is < 0 or > 9999)
        {
            errors.Add("SortOrder", "Sort order must be between 0 and 9999.");
        }

        errors.ThrowIfAny();
    }

    private static async Task EnsureNameUniqueAsync(MenuCatalogDbContext db, Guid tenantId, string name, Guid? excludeId, CancellationToken ct)
    {
        var normalized = name.Trim().ToLower();
        if (await db.Categories.AnyAsync(c => c.TenantId == tenantId && c.Name.ToLower() == normalized && c.Id != excludeId, ct))
        {
            throw new ConflictException($"Category '{name.Trim()}' already exists in this brand.", "category.duplicate_name");
        }
    }

    /// <summary>A category of another tenant is reported as not found, never as forbidden: its existence is not revealed.</summary>
    internal static async Task<Category> FindAsync(MenuCatalogDbContext db, ICurrentUser user, Guid id, CancellationToken ct)
    {
        var tenantId = TenantScope.Require(user);
        return await db.Categories.FirstOrDefaultAsync(c => c.Id == id && c.TenantId == tenantId, ct) ?? throw new NotFoundException("Category", id);
    }
}
