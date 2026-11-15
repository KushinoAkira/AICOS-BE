using System.Security.Cryptography;
using System.Text;
using AICOS.BuildingBlocks.Messaging;
using AICOS.Contracts.Events;
using AICOS.Ordering.Api.Domain.Entities;
using AICOS.Ordering.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AICOS.Ordering.Api.Features.Analytics;

public sealed class AprioriMiner(
    OrderingDbContext db,
    IAprioriClient client,
    IEventBus bus,
    IOptions<AprioriOptions> options,
    TimeProvider clock,
    ILogger<AprioriMiner> logger)
{
    public async Task<AprioriMineResult> MineTenantAsync(Guid tenantId, Guid? branchId, CancellationToken ct)
    {
        var opt = options.Value;
        var since = clock.GetUtcNow().UtcDateTime.AddDays(-Math.Max(7, opt.LookbackDays));
        await BasketUpsert.BackfillAsync(db, tenantId, since, ct);

        var basketsQuery = db.OrderBaskets.AsNoTracking().Include(b => b.Items)
            .Where(b => b.TenantId == tenantId && b.PaidAtUtc >= since);
        if (branchId is not null)
        {
            basketsQuery = basketsQuery.Where(b => b.BranchId == branchId);
        }

        var baskets = await basketsQuery.ToListAsync(ct);
        var nameById = baskets.SelectMany(b => b.Items).GroupBy(i => i.ProductId)
            .ToDictionary(g => g.Key, g => g.First().ProductName);

        var transactions = baskets
            .Select(b => b.Items.Select(i => i.ProductId.ToString("D")).Distinct().ToList())
            .Where(t => t.Count >= 2)
            .Cast<IReadOnlyList<string>>()
            .ToList();

        if (transactions.Count < 3)
        {
            logger.LogInformation("Apriori skip tenant {TenantId}: only {Count} multi-item baskets", tenantId, transactions.Count);
            return new AprioriMineResult(0, 0, 0);
        }

        AprioriMineResponse mined;
        try
        {
            mined = await client.MineAsync(transactions, opt.MinSupport, opt.MinConfidence, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Apriori AI service unavailable for tenant {TenantId}", tenantId);
            return new AprioriMineResult(transactions.Count, 0, 0);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var existing = await db.AssociationRules
            .Where(r => r.TenantId == tenantId && r.BranchId == branchId)
            .ToListAsync(ct);
        var byKey = existing.ToDictionary(r => r.PairKey);
        var promoted = 0;

        foreach (var rule in mined.Rules.Where(r => r.Antecedent.Count > 0 && r.Consequent.Count > 0))
        {
            if (!TryParseIds(rule.Antecedent, out var antIds) || !TryParseIds(rule.Consequent, out var consIds))
            {
                continue;
            }

            var pairKey = PairKey(antIds, consIds);
            var antNames = string.Join(", ", antIds.Select(id => nameById.GetValueOrDefault(id, id.ToString("N")[..8])));
            var consNames = string.Join(", ", consIds.Select(id => nameById.GetValueOrDefault(id, id.ToString("N")[..8])));
            var antCsv = string.Join(",", antIds.Select(id => id.ToString("D")));
            var consCsv = string.Join(",", consIds.Select(id => id.ToString("D")));

            if (byKey.TryGetValue(pairKey, out var row))
            {
                row.Refresh(antNames, consNames, rule.Support, rule.Confidence, rule.Lift, rule.TransactionCount, now);
            }
            else
            {
                row = new AssociationRule(tenantId, branchId, pairKey, antCsv, consCsv, antNames, consNames,
                    rule.Support, rule.Confidence, rule.Lift, rule.TransactionCount, now);
                db.AssociationRules.Add(row);
                byKey[pairKey] = row;
            }

            if (!row.VoucherCreated
                && rule.Support >= opt.PromoMinSupport
                && rule.Confidence >= opt.PromoMinConfidence
                && rule.Lift >= opt.PromoMinLift)
            {
                await bus.PublishAsync(new AssociationRulePromotedEvent(
                    tenantId, branchId, row.Id, pairKey, antNames, consNames,
                    rule.Support, rule.Confidence, rule.Lift, rule.TransactionCount), ct);
                row.MarkVoucherCreated();
                promoted++;
            }
        }

        await db.SaveChangesAsync(ct);
        return new AprioriMineResult(transactions.Count, mined.Rules.Count, promoted);
    }

    public async Task<int> MineAllTenantsAsync(CancellationToken ct)
    {
        var since = clock.GetUtcNow().UtcDateTime.AddDays(-Math.Max(7, options.Value.LookbackDays));
        var tenants = await db.OrderBaskets.AsNoTracking()
            .Where(b => b.PaidAtUtc >= since)
            .Select(b => b.TenantId)
            .Distinct()
            .ToListAsync(ct);
        var total = 0;
        foreach (var tenant in tenants)
        {
            var result = await MineTenantAsync(tenant, branchId: null, ct);
            total += result.RuleCount;
        }

        return total;
    }

    private static bool TryParseIds(IEnumerable<string> raw, out List<Guid> ids)
    {
        ids = [];
        foreach (var s in raw)
        {
            if (!Guid.TryParse(s, out var id))
            {
                return false;
            }

            ids.Add(id);
        }

        ids = ids.Distinct().OrderBy(x => x).ToList();
        return ids.Count > 0;
    }

    private static string PairKey(IReadOnlyList<Guid> antecedent, IReadOnlyList<Guid> consequent)
    {
        var payload = string.Join("|", antecedent.Select(g => g.ToString("D")))
            + "=>"
            + string.Join("|", consequent.Select(g => g.ToString("D")));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash)[..40];
    }
}

public sealed record AprioriMineResult(int BasketCount, int RuleCount, int PromotedCount);
