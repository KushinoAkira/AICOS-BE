using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Persistence;
using AICOS.BuildingBlocks.Security;
using AICOS.BuildingBlocks.Validation;
using AICOS.MenuCatalog.Api.Domain;
using AICOS.MenuCatalog.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.MenuCatalog.Api.Features.Categories;

/// <summary>Body of POST/PUT. <see cref="BranchId"/> is only read for a BrandAdmin; staff always use their own branch.</summary>
public sealed record CategoryRequest(Guid? BranchId, string? Name, int? SortOrder);

public sealed record ChangeCategoryStatusRequest(CategoryStatus? Status);

public sealed record CategoryResponse(Guid Id, Guid BranchId, string Name, int SortOrder, CategoryStatus Status, int ProductCount)
{
    public static CategoryResponse From(Category c, int productCount = 0) => new(c.Id, c.BranchId, c.Name, c.SortOrder, c.Status, productCount);
}

/// <summary>UC-06 Manage Menu - categories. Routes: /api/v1/menu/categories. Read: all staff. Write: BrandAdmin/Manager.</summary>
public static class CategoryEndpoints
{
    public static void MapCategoryEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/categories").WithTags("Categories");

        group.MapGet("/", ListAsync).RequireAuthorization(AicosPolicies.Staff);
        group.MapGet("/{id:guid}", GetAsync).RequireAuthorization(AicosPolicies.Staff).WithName("GetCategory");
        group.MapPost("/", CreateAsync).RequireAuthorization(AicosPolicies.Management);
        group.MapPut("/{id:guid}", UpdateAsync).RequireAuthorization(AicosPolicies.Management);
        group.MapPatch("/{id:guid}/status", ChangeStatusAsync).RequireAuthorization(AicosPolicies.Management);
        group.MapDelete("/{id:guid}", DeleteAsync).RequireAuthorization(AicosPolicies.Management);
    }

    private static async Task<List<CategoryResponse>> ListAsync(
        MenuCatalogDbContext db, ICurrentUser user, Guid? branchId, CategoryStatus? status, CancellationToken ct)
    {
        var branch = BranchAccess.ResolveFilter(user, branchId);
        var query = db.Categories.AsNoTracking();
        if (branch is not null)
        {
            query = query.Where(c => c.BranchId == branch);
        }

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
        var count = await db.Products.CountAsync(p => p.CategoryId == id, ct);
        return CategoryResponse.From(category, count);
    }

    private static async Task<IResult> CreateAsync(CategoryRequest request, MenuCatalogDbContext db, ICurrentUser user, CancellationToken ct)
    {
        Validate(request);
        var branchId = BranchAccess.Resolve(user, request.BranchId);
        await EnsureNameUniqueAsync(db, branchId, request.Name!, null, ct);

        var category = new Category(branchId, request.Name!, request.SortOrder ?? 0);
        db.Categories.Add(category);
        await db.SaveChangesAsync(ct);

        return Results.CreatedAtRoute("GetCategory", new { id = category.Id }, CategoryResponse.From(category));
    }

    private static async Task<CategoryResponse> UpdateAsync(
        Guid id, CategoryRequest request, MenuCatalogDbContext db, ICurrentUser user, CancellationToken ct)
    {
        Validate(request);
        var category = await FindAsync(db, user, id, ct);
        await EnsureNameUniqueAsync(db, category.BranchId, request.Name!, id, ct);

        category.Update(request.Name!, request.SortOrder ?? category.SortOrder);
        await db.SaveChangesAsync(ct);
        return CategoryResponse.From(category, await db.Products.CountAsync(p => p.CategoryId == id, ct));
    }

    private static async Task<CategoryResponse> ChangeStatusAsync(
        Guid id, ChangeCategoryStatusRequest request, MenuCatalogDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        CommonRules.RequiredEnum(errors, "Status", request.Status, "Status");
        errors.ThrowIfAny();

        var category = await FindAsync(db, user, id, ct);
        category.ChangeStatus(request.Status!.Value);
        await db.SaveChangesAsync(ct);
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

    private static async Task EnsureNameUniqueAsync(MenuCatalogDbContext db, Guid branchId, string name, Guid? excludeId, CancellationToken ct)
    {
        var normalized = name.Trim().ToLower();
        if (await db.Categories.AnyAsync(c => c.BranchId == branchId && c.Name.ToLower() == normalized && c.Id != excludeId, ct))
        {
            throw new ConflictException($"Category '{name.Trim()}' already exists in this branch.", "category.duplicate_name");
        }
    }

    internal static async Task<Category> FindAsync(MenuCatalogDbContext db, ICurrentUser user, Guid id, CancellationToken ct)
    {
        var category = await db.Categories.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException("Category", id);
        BranchAccess.EnsureCanAccess(user, category.BranchId);
        return category;
    }
}
