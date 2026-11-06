using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.Ordering.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.Ordering.Api.Features.Orders;

/// <summary>Bounded actor metadata lookup; no dependency on Identity tables.</summary>
public static class OrderCreatorEndpoints
{
    public static void MapOrderCreatorEndpoints(this RouteGroupBuilder api) =>
        api.MapGet("/staff/creators", async (string ids, Guid? branchId, OrderingDbContext db, ICurrentUser user, CancellationToken ct) =>
        {
            var tenant = TenantScope.Require(user);
            var branch = BranchAccess.ResolveFilter(user, branchId);
            var parts = ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length is < 1 or > 50 || parts.Any(x => !Guid.TryParse(x, out _)))
                throw new ValidationFailedException("ids", "Supply 1-50 order ids.");
            var wanted = parts.Select(Guid.Parse).Distinct().ToArray();
            return await db.Orders.AsNoTracking().Where(o => o.TenantId == tenant && wanted.Contains(o.Id)
                && (branch == null || o.BranchId == branch))
                .Select(o => new { OrderId = o.Id, o.CashierId, o.CustomerId, o.CreatedAtUtc, o.TableNumber }).ToListAsync(ct);
        }).WithTags("Orders").RequireAuthorization(AicosPolicies.Staff);
}