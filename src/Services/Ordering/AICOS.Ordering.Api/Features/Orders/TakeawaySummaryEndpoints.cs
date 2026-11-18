using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.Ordering.Api.Domain.Enums;
using AICOS.Ordering.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.Ordering.Api.Features.Orders;

public sealed record TakeawaySummaryResponse(Guid BranchId, int ActiveOrders, int ReadyOrders, DateTime ObservedAtUtc, bool ReadyOrdersKnown);

/// <summary>Whole branch counts, never inferred from a paginated order list. SELECT only.</summary>
public static class TakeawaySummaryEndpoints
{
    public static void MapTakeawaySummary(this RouteGroupBuilder api) =>
        api.MapGet("/staff/takeaway-summary", async (Guid branchId, OrderingDbContext db,
            ICurrentUser user, TimeProvider clock, CancellationToken ct) =>
        {
            if (branchId == Guid.Empty) throw new ValidationFailedException("branchId", "Choose a branch.");
            var tenant = TenantScope.Require(user);
            BranchAccess.ResolveFilter(user, branchId);
            var counts = await db.Orders.AsNoTracking().Where(o => o.TenantId == tenant && o.BranchId == branchId
                && o.Type == OrderType.Takeaway && o.DeliveredAtUtc == null
                && (o.Status == OrderStatus.Pending || o.Status == OrderStatus.Confirmed
                    || o.Status == OrderStatus.Preparing || o.Status == OrderStatus.Ready))
                .GroupBy(_ => 1)
                .Select(g => new { Active = g.Count(), Ready = g.Count(o => o.Status == OrderStatus.Ready),
                    Cups = g.Count(o => o.FulfillmentMode != AICOS.Contracts.Serving.FulfillmentModes.WholeOrder) })
                .SingleOrDefaultAsync(ct);
            return new TakeawaySummaryResponse(branchId, counts?.Active ?? 0, counts?.Ready ?? 0, clock.GetUtcNow().UtcDateTime, (counts?.Cups ?? 0) == 0);
        }).WithTags("Orders").RequireAuthorization(AicosPolicies.Staff);
}
