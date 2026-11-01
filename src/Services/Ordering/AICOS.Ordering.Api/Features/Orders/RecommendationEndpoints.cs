using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.Ordering.Api.Domain.Entities;
using AICOS.Ordering.Api.Domain.Enums;
using AICOS.Ordering.Api.Persistence;
using AICOS.Ordering.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace AICOS.Ordering.Api.Features.Orders;

/// <summary><see cref="Quantity"/> is how many were sold at the branch, or how many this customer has ordered.</summary>
public sealed record RecommendedProduct(Guid ProductId, string ProductName, int Quantity);

public sealed record RecommendationsResponse(IReadOnlyList<RecommendedProduct> ForYou, IReadOnlyList<RecommendedProduct> BestSellers);

/// <summary>
/// UC-54: what the menu suggests. GET /api/v1/orders/recommendations?branchId=. No model behind it: <c>BestSellers</c> are the products the branch
/// sold most in the last <see cref="BestSellerDays"/> days, and <c>ForYou</c> what a signed-in customer has ordered most at this brand.
/// Open to anyone because guests at a table read it too; it only shows names and totals, never whose orders they were.
/// </summary>
public static class RecommendationEndpoints
{
    private const int BestSellerDays = 30;
    private const int DefaultLimit = 6;
    private const int MaxLimit = 20;

    private static readonly OrderStatus[] NotSold = [OrderStatus.Cancelled, OrderStatus.NoShow, OrderStatus.Unfulfillable];

    public static void MapRecommendationEndpoints(this RouteGroupBuilder group) =>
        group.MapGet("/recommendations", GetAsync).AllowAnonymous();

    private static async Task<RecommendationsResponse> GetAsync(
        Guid? branchId, int? limit, OrderingDbContext db, ICurrentUser user, ShopTime clock, CancellationToken ct)
    {
        if (branchId is null)
        {
            throw new ValidationFailedException(nameof(branchId), "branchId is required.");
        }

        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var since = clock.UtcNow.AddDays(-BestSellerDays);
        var bestSellers = await RankAsync(db.Orders.AsNoTracking()
            .Where(o => o.BranchId == branchId && o.CreatedAtUtc >= since && !NotSold.Contains(o.Status)
                && (o.PaymentStatus == PaymentStatus.Paid || o.Status == OrderStatus.Completed))
            .SelectMany(o => o.Items), take, ct);

        List<RecommendedProduct> forYou = [];
        if (user.IsInRole(AicosRoles.Customer) && user.UserId is { } customerId && user.TenantId is { } tenantId)
        {
            forYou = await RankAsync(db.Orders.AsNoTracking()
                .Where(o => o.TenantId == tenantId && o.CustomerId == customerId && !NotSold.Contains(o.Status))
                .SelectMany(o => o.Items), take, ct);
        }

        return new RecommendationsResponse(forYou, bestSellers);
    }

    private static async Task<List<RecommendedProduct>> RankAsync(IQueryable<OrderItem> items, int take, CancellationToken ct)
    {
        var ranked = await items
            .GroupBy(i => i.ProductId)
            .Select(g => new { ProductId = g.Key, ProductName = g.Max(i => i.ProductName)!, Quantity = g.Sum(i => i.Quantity) })
            .OrderByDescending(p => p.Quantity).ThenBy(p => p.ProductName)
            .Take(take)
            .ToListAsync(ct);
        return ranked.ConvertAll(p => new RecommendedProduct(p.ProductId, p.ProductName, p.Quantity));
    }
}
