using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.MenuCatalog.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.MenuCatalog.Api.Features.Catalog;

public sealed class CatalogQueryService(MenuCatalogDbContext dbContext)
{
    public async Task<CatalogResponse> GetAsync(
        ICurrentUser currentUser,
        Guid? categoryId,
        string? search,
        CancellationToken cancellationToken)
    {
        var branchId = currentUser.BranchId is { } id && id != Guid.Empty
            ? id
            : throw new ForbiddenException("A branch-scoped access token is required.");
        var normalizedSearch = search?.Trim();
        if (normalizedSearch?.Length > 200)
        {
            throw new ValidationFailedException("search", "Search must not exceed 200 characters.");
        }

        var query = dbContext.BranchProducts
            .AsNoTracking()
            .AsSplitQuery()
            .Include(item => item.Product)
                .ThenInclude(product => product.Category)
            .Include(item => item.Product)
                .ThenInclude(product => product.Variants)
            .Include(item => item.Product)
                .ThenInclude(product => product.OptionGroups)
                    .ThenInclude(group => group.Options)
            .Where(item =>
                item.BranchId == branchId &&
                item.IsAvailable != false &&
                item.Product.IsActive != false &&
                item.Product.Category.IsActive != false &&
                item.Product.TenantId == item.Product.Category.TenantId);

        if (categoryId is not null)
        {
            query = query.Where(item => item.Product.CategoryId == categoryId);
        }

        if (!string.IsNullOrWhiteSpace(normalizedSearch))
        {
            var pattern = $"%{normalizedSearch}%";
            query = query.Where(item => EF.Functions.ILike(item.Product.Name, pattern));
        }

        var products = await query.ToListAsync(cancellationToken);
        var categories = products
            .GroupBy(item => new
            {
                item.Product.Category.Id,
                item.Product.Category.Name,
                item.Product.Category.SortOrder,
            })
            .OrderBy(group => group.Key.SortOrder)
            .ThenBy(group => group.Key.Name)
            .Select(group => new CategoryResponse(
                group.Key.Id,
                group.Key.Name,
                group.Key.SortOrder ?? 0,
                group.Select(item => item.Product)
                    .DistinctBy(product => product.Id)
                    .OrderBy(product => product.Name)
                    .Select(ToProductResponse)
                    .ToList()))
            .ToList();

        return new CatalogResponse(branchId, categories);
    }

    private static ProductResponse ToProductResponse(Domain.Product product) =>
        new(
            product.Id,
            product.Name,
            product.Description,
            product.ImageUrl,
            product.Variants
                .Where(variant => variant.IsActive != false)
                .OrderBy(variant => variant.Price)
                .ThenBy(variant => variant.SizeName)
                .Select(variant => new VariantResponse(variant.Id, variant.SizeName, variant.Price))
                .ToList(),
            product.OptionGroups
                .OrderBy(group => group.Name)
                .Select(group => new OptionGroupResponse(
                    group.Id,
                    group.Name,
                    group.MinSelect ?? 0,
                    group.MaxSelect ?? 1,
                    group.Options
                        .Where(option => option.IsActive != false)
                        .OrderBy(option => option.Name)
                        .Select(option => new OptionResponse(
                            option.Id,
                            option.Name,
                            option.ExtraPrice ?? 0m))
                        .ToList()))
                .ToList());
}
