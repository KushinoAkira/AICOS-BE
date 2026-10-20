using AICOS.BuildingBlocks.Exceptions;
using AICOS.MenuCatalog.Api.Domain;
using AICOS.MenuCatalog.Api.Features.Products;
using AICOS.MenuCatalog.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.MenuCatalog.Api.Features.PublicMenu;

public sealed record PublicProductResponse(
    Guid Id,
    string Name,
    string? Description,
    string? ImageUrl,
    decimal Price,
    decimal? PromotionPrice,
    string? PromotionName,
    List<string> Tags,
    List<OptionGroupResponse> OptionGroups);

public sealed record PublicCategoryResponse(Guid Id, string Name, List<PublicProductResponse> Products);

public sealed record PublicMenuResponse(Guid BranchId, List<PublicCategoryResponse> Categories);

/// <summary>Minimal product info other services need.</summary>
public sealed record ProductLookupResponse(Guid Id, Guid BranchId, string Name, decimal BasePrice, bool IsAvailable);

/// <summary>
/// UC-47 Guest views the public menu without login (also the QR menu). Routes: /api/v1/menu/public.
/// Only active categories and available products; running promotions are applied to show the discounted price.
/// </summary>
public static class PublicMenuEndpoints
{
    public static void MapPublicMenuEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/public").WithTags("Public menu").AllowAnonymous();

        group.MapGet("/branches/{branchId:guid}/menu", GetMenuAsync);
        group.MapGet("/products/{id:guid}", GetProductAsync);
    }

    private static async Task<PublicMenuResponse> GetMenuAsync(Guid branchId, MenuCatalogDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;

        var categories = await db.Categories.AsNoTracking()
            .Where(c => c.BranchId == branchId && c.Status == CategoryStatus.Active)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .ToListAsync(ct);

        var products = await db.Products.AsNoTracking()
            .Include(p => p.OptionGroups).ThenInclude(g => g.Options).AsSplitQuery()
            .Where(p => p.BranchId == branchId && p.IsAvailable)
            .OrderBy(p => p.Name)
            .ToListAsync(ct);

        var promotions = await db.Promotions.AsNoTracking().Include(p => p.Products)
            .Where(p => p.BranchId == branchId && p.Status == PromotionStatus.Active && p.StartDateUtc <= now && now < p.EndDateUtc)
            .ToListAsync(ct);

        return new PublicMenuResponse(branchId, categories
            .Select(c => new PublicCategoryResponse(c.Id, c.Name, products
                .Where(p => p.CategoryId == c.Id)
                .Select(p => ToPublic(p, promotions))
                .ToList()))
            .Where(c => c.Products.Count > 0)
            .ToList());
    }

    private static async Task<ProductLookupResponse> GetProductAsync(Guid id, MenuCatalogDbContext db, CancellationToken ct)
    {
        var p = await db.Products.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Product", id);
        return new ProductLookupResponse(p.Id, p.BranchId, p.Name, p.BasePrice, p.IsAvailable);
    }

    private static PublicProductResponse ToPublic(Product p, List<Promotion> promotions)
    {
        // Best (lowest) price among running promotions that include this product.
        var best = promotions
            .Where(promo => promo.Products.Any(x => x.ProductId == p.Id))
            .Select(promo => (Promotion: promo, Price: promo.Apply(p.BasePrice)))
            .OrderBy(x => x.Price)
            .FirstOrDefault();

        return new PublicProductResponse(
            p.Id, p.Name, p.Description, p.ImageUrl, p.BasePrice,
            best.Promotion is null ? null : best.Price,
            best.Promotion?.Name,
            p.Tags,
            ProductResponse.MapGroups(p));
    }
}
