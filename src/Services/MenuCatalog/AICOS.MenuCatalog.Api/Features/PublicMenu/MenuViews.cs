using AICOS.MenuCatalog.Api.Domain;
using AICOS.MenuCatalog.Api.Features.Products;
using AICOS.MenuCatalog.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.MenuCatalog.Api.Features.PublicMenu;

/// <summary>
/// One product as a branch sells it. <see cref="Price"/> is what the customer pays before options: the branch price when it has
/// one, otherwise the brand base price (BR-15, BR-17).
/// </summary>
public sealed record MenuProductResponse(
    Guid Id,
    string Name,
    string? Description,
    string? ImageUrl,
    decimal BasePrice,
    decimal Price,
    bool HasBranchPrice,
    bool IsAvailable,
    List<string> Tags,
    List<OptionGroupResponse> OptionGroups);

public sealed record MenuCategoryResponse(Guid Id, string Name, List<MenuProductResponse> Products);

public sealed record BranchMenuResponse(Guid BranchId, List<MenuCategoryResponse> Categories);

/// <summary>Builds the menu a branch actually sells (UC-15): published products only, in categories that are shown.</summary>
internal static class MenuViews
{
    /// <param name="includeSoldOut">False for customers (sold-out items cannot be ordered, BR-16); true for staff screens, which show them as sold out.</param>
    public static async Task<BranchMenuResponse> BuildAsync(MenuCatalogDbContext db, Guid branchId, bool includeSoldOut, CancellationToken ct)
    {
        var settings = await db.BranchProducts.AsNoTracking()
            .Where(b => b.BranchId == branchId && b.IsPublished && (includeSoldOut || b.IsAvailable))
            .ToListAsync(ct);
        var productIds = settings.Select(s => s.ProductId).ToList();

        var products = await db.Products.AsNoTracking()
            .Include(p => p.OptionGroups).ThenInclude(g => g.Options).AsSplitQuery()
            .Where(p => productIds.Contains(p.Id))
            .ToListAsync(ct);

        var categoryIds = products.Select(p => p.CategoryId).Distinct().ToList();
        var categories = await db.Categories.AsNoTracking()
            .Where(c => categoryIds.Contains(c.Id) && c.Status == CategoryStatus.Active)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .ToListAsync(ct);

        var bySettings = settings.ToDictionary(s => s.ProductId);
        return new BranchMenuResponse(branchId, categories
            .Select(c => new MenuCategoryResponse(c.Id, c.Name, products
                .Where(p => p.CategoryId == c.Id)
                .OrderBy(p => p.Name)
                .Select(p => ToResponse(p, bySettings[p.Id]))
                .ToList()))
            .ToList());
    }

    public static decimal EffectivePrice(Product product, BranchProduct setting) => setting.PriceOverride ?? product.BasePrice;

    private static MenuProductResponse ToResponse(Product p, BranchProduct setting) => new(
        p.Id, p.Name, p.Description, p.ImageUrl, p.BasePrice, EffectivePrice(p, setting), setting.PriceOverride is not null, setting.IsAvailable,
        p.Tags, ProductResponse.MapGroups(p));
}
