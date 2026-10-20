using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.BuildingBlocks.Validation;
using AICOS.MenuCatalog.Api.Domain;
using AICOS.MenuCatalog.Api.Features.Products;
using AICOS.MenuCatalog.Api.Features.PublicMenu;
using AICOS.MenuCatalog.Api.Infrastructure;
using AICOS.MenuCatalog.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.MenuCatalog.Api.Features.Branches;

/// <summary>PUT /products/{id}/branches/{branchId}: whether the branch sells the product and at what price. A null price means the base price.</summary>
public sealed record BranchProductRequest(bool? IsPublished, decimal? PriceOverride);

/// <summary>POST /publications: publish (default) or unpublish products on several branches at once. A null product list means every product of the brand.</summary>
public sealed record PublicationRequest(List<Guid>? BranchIds, List<Guid>? ProductIds, bool? Publish);

public sealed record ChangeAvailabilityRequest(bool? IsAvailable);

public sealed record BranchProductResponse(
    Guid BranchId, Guid ProductId, string ProductName, bool IsPublished, bool IsAvailable, decimal BasePrice, decimal? PriceOverride, decimal EffectivePrice);

public sealed record PublicationResult(int Branches, int Products, int Changed);

/// <summary>
/// UC-15 Publish Menu to Branches, UC-24 Update Product Availability and the per-branch price (BR-15). The brand decides what a
/// branch sells and at what price (BrandAdmin only, BR-12); the branch decides what is sold out right now. Every change clears
/// the cached menu of the branch and sends a MenuChangedEvent so QR ordering, POS and takeaway refresh (NFR-25).
/// </summary>
public static class BranchMenuEndpoints
{
    private const int MaxBranchesPerPublication = 50;
    private const decimal MaxPrice = ProductValidator.MaxPrice;

    public static void MapBranchMenuEndpoints(this RouteGroupBuilder api)
    {
        api.MapPut("/products/{id:guid}/branches/{branchId:guid}", SetBranchProductAsync).WithTags("Branch menu").RequireAuthorization(AicosPolicies.AdminOnly);
        api.MapGet("/products/{id:guid}/branches", ListBranchSettingsAsync).WithTags("Branch menu").RequireAuthorization(AicosPolicies.Management);
        api.MapPost("/publications", PublishAsync).WithTags("Branch menu").RequireAuthorization(AicosPolicies.AdminOnly);

        api.MapGet("/branches/{branchId:guid}/menu", GetBranchMenuAsync).WithTags("Branch menu").RequireAuthorization(AicosPolicies.Staff);
        api.MapPatch("/branches/{branchId:guid}/products/{productId:guid}/availability", ChangeAvailabilityAsync)
            .WithTags("Branch menu").RequireAuthorization(AicosPolicies.AvailabilityEditors);
    }

    /// <summary>The staff view of a branch menu: published products including the ones that are sold out right now.</summary>
    private static async Task<BranchMenuResponse> GetBranchMenuAsync(
        Guid branchId, MenuCatalogDbContext db, MenuCache cache, ICurrentUser user, IBranchDirectory directory, CancellationToken ct)
    {
        await BranchScope.ResolveAsync(user, directory, branchId, requireActive: false, ct);
        return await cache.GetOrBuildAsync(branchId, "staff", () => MenuViews.BuildAsync(db, branchId, includeSoldOut: true, ct), ct);
    }

    private static async Task<BranchProductResponse> SetBranchProductAsync(
        Guid id,
        Guid branchId,
        BranchProductRequest request,
        MenuCatalogDbContext db,
        ICurrentUser user,
        IBranchDirectory directory,
        MenuChangeNotifier notifier,
        CancellationToken ct)
    {
        var errors = new ValidationErrors();
        if (request.IsPublished is null)
        {
            errors.Add("IsPublished", "IsPublished is required (true or false).");
        }

        if (request.PriceOverride is <= 0 or > MaxPrice)
        {
            errors.Add("PriceOverride", $"The branch price must be greater than 0 and at most {MaxPrice:N0}, or empty to use the base price.");
        }

        errors.ThrowIfAny();

        var product = await ProductEndpoints.FindAsync(db, user, id, ct);
        await BranchScope.EnsureUsableAsync(directory, branchId, requireActive: request.IsPublished == true, ct);

        var setting = await GetOrAddAsync(db, product.TenantId, branchId, id, ct);
        if (request.IsPublished == true)
        {
            setting.Publish();
        }
        else
        {
            setting.Unpublish();
        }

        setting.SetPriceOverride(request.PriceOverride);
        await db.SaveChangesAsync(ct);

        await notifier.BranchChangedAsync(product.TenantId, branchId, "branch_product.changed", ct);
        return ToResponse(product, setting);
    }

    private static async Task<List<BranchProductResponse>> ListBranchSettingsAsync(
        Guid id, MenuCatalogDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var product = await ProductEndpoints.FindAsync(db, user, id, ct);
        var query = db.BranchProducts.AsNoTracking().Where(b => b.ProductId == id && b.TenantId == product.TenantId);

        // A BranchManager only sees the settings of their own branch (BR-05).
        if (user.IsInRole(AicosRoles.BranchManager))
        {
            var own = user.BranchId ?? throw new ForbiddenException("Your account is not assigned to a branch.");
            query = query.Where(b => b.BranchId == own);
        }

        var settings = await query.OrderBy(b => b.BranchId).ToListAsync(ct);
        return settings.Select(s => ToResponse(product, s)).ToList();
    }

    private static async Task<PublicationResult> PublishAsync(
        PublicationRequest request,
        MenuCatalogDbContext db,
        ICurrentUser user,
        IBranchDirectory directory,
        MenuChangeNotifier notifier,
        CancellationToken ct)
    {
        var errors = new ValidationErrors();
        var branchIds = (request.BranchIds ?? []).Distinct().ToList();
        if (branchIds.Count is 0 or > MaxBranchesPerPublication)
        {
            errors.Add("BranchIds", $"Choose 1-{MaxBranchesPerPublication} branches.");
        }

        if (request.ProductIds is { Count: 0 })
        {
            errors.Add("ProductIds", "ProductIds must list at least one product, or be left out to use every product.");
        }

        errors.ThrowIfAny();

        var tenantId = TenantScope.Require(user);
        var publish = request.Publish ?? true;

        var products = await db.Products.AsNoTracking()
            .Where(p => p.TenantId == tenantId && (request.ProductIds == null || request.ProductIds.Contains(p.Id)))
            .Select(p => p.Id)
            .ToListAsync(ct);

        if (request.ProductIds is not null && products.Count != request.ProductIds.Distinct().Count())
        {
            throw new ValidationFailedException("ProductIds", "One or more products do not exist in this brand.");
        }

        foreach (var branchId in branchIds)
        {
            await BranchScope.EnsureUsableAsync(directory, branchId, requireActive: publish, ct);
        }

        var changed = 0;
        foreach (var branchId in branchIds)
        {
            var existing = await db.BranchProducts.Where(b => b.BranchId == branchId && b.TenantId == tenantId).ToDictionaryAsync(b => b.ProductId, ct);
            foreach (var productId in products)
            {
                if (!existing.TryGetValue(productId, out var setting))
                {
                    if (!publish)
                    {
                        continue;
                    }

                    setting = new BranchProduct(tenantId, branchId, productId);
                    db.BranchProducts.Add(setting);
                }

                var before = setting.IsPublished;
                if (publish)
                {
                    setting.Publish();
                }
                else
                {
                    setting.Unpublish();
                }

                if (before != setting.IsPublished)
                {
                    changed++;
                }
            }
        }

        await db.SaveChangesAsync(ct);

        foreach (var branchId in branchIds)
        {
            await notifier.BranchChangedAsync(tenantId, branchId, publish ? "menu.published" : "menu.unpublished", ct);
        }

        return new PublicationResult(branchIds.Count, products.Count, changed);
    }

    /// <summary>
    /// UC-24: mark a product Sold Out or available again at one branch. Staff can only act on their own branch (BR-05); the
    /// product must be on that branch menu. There is no stock tracking, so this is a manual switch (BR-16).
    /// </summary>
    private static async Task<BranchProductResponse> ChangeAvailabilityAsync(
        Guid branchId,
        Guid productId,
        ChangeAvailabilityRequest request,
        MenuCatalogDbContext db,
        ICurrentUser user,
        IBranchDirectory directory,
        MenuChangeNotifier notifier,
        CancellationToken ct)
    {
        if (request.IsAvailable is null)
        {
            throw new ValidationFailedException("IsAvailable", "IsAvailable is required (true or false).");
        }

        await BranchScope.ResolveAsync(user, directory, branchId, requireActive: false, ct);
        var tenantId = TenantScope.Require(user);

        var product = await ProductEndpoints.FindAsync(db, user, productId, ct);
        var setting = await db.BranchProducts.FirstOrDefaultAsync(b => b.BranchId == branchId && b.ProductId == productId && b.TenantId == tenantId, ct);
        if (setting is null || !setting.IsPublished)
        {
            throw new BusinessRuleException("product.not_published", $"'{product.Name}' is not on this branch menu.");
        }

        setting.SetAvailability(request.IsAvailable.Value);
        await db.SaveChangesAsync(ct);

        await notifier.BranchChangedAsync(tenantId, branchId, request.IsAvailable.Value ? "product.available" : "product.sold_out", ct);
        return ToResponse(product, setting);
    }

    private static async Task<BranchProduct> GetOrAddAsync(MenuCatalogDbContext db, Guid tenantId, Guid branchId, Guid productId, CancellationToken ct)
    {
        var setting = await db.BranchProducts.FirstOrDefaultAsync(b => b.BranchId == branchId && b.ProductId == productId, ct);
        if (setting is null)
        {
            setting = new BranchProduct(tenantId, branchId, productId);
            db.BranchProducts.Add(setting);
        }

        return setting;
    }

    private static BranchProductResponse ToResponse(Product product, BranchProduct setting) => new(
        setting.BranchId, product.Id, product.Name, setting.IsPublished, setting.IsAvailable, product.BasePrice, setting.PriceOverride,
        MenuViews.EffectivePrice(product, setting));
}
