using AICOS.BuildingBlocks.Security;
using AICOS.Ordering.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.Ordering.Api.Features.Analytics;

public sealed record AssociationRuleResponse(
    Guid Id, Guid? BranchId, string AntecedentNames, string ConsequentNames,
    double Support, double Confidence, double Lift, int TransactionCount,
    DateTime MinedAtUtc, bool VoucherCreated);

public sealed record AprioriRunResponse(int BasketCount, int RuleCount, int PromotedCount);

public static class AprioriEndpoints
{
    public static void MapAprioriEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/analytics").WithTags("Apriori analytics");
        group.MapGet("/association-rules", ListAsync).RequireAuthorization(AicosPolicies.Management);
        group.MapPost("/apriori/run", RunAsync).RequireAuthorization(AicosPolicies.AdminOnly);
    }

    private static async Task<List<AssociationRuleResponse>> ListAsync(
        OrderingDbContext db, ICurrentUser user, Guid? branchId, int? limit, CancellationToken ct)
    {
        var tenant = TenantScope.Require(user);
        var take = Math.Clamp(limit ?? 50, 1, 200);
        var query = db.AssociationRules.AsNoTracking().Where(r => r.TenantId == tenant);
        if (branchId is not null)
        {
            query = query.Where(r => r.BranchId == null || r.BranchId == branchId);
        }

        return await query.OrderByDescending(r => r.Lift).ThenByDescending(r => r.Confidence)
            .Take(take)
            .Select(r => new AssociationRuleResponse(
                r.Id, r.BranchId, r.AntecedentNames, r.ConsequentNames,
                r.Support, r.Confidence, r.Lift, r.TransactionCount, r.MinedAtUtc, r.VoucherCreated))
            .ToListAsync(ct);
    }

    private static async Task<AprioriRunResponse> RunAsync(
        AprioriMiner miner, ICurrentUser user, Guid? branchId, CancellationToken ct)
    {
        var tenant = TenantScope.Require(user);
        var result = await miner.MineTenantAsync(tenant, branchId, ct);
        return new AprioriRunResponse(result.BasketCount, result.RuleCount, result.PromotedCount);
    }
}
