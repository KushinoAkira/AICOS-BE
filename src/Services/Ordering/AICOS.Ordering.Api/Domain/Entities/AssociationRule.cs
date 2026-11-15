using AICOS.BuildingBlocks.Domain;

namespace AICOS.Ordering.Api.Domain.Entities;

/// <summary>Apriori association rule mined for a brand (optionally scoped to a branch).</summary>
public sealed class AssociationRule : AuditableEntity<Guid>
{
    private AssociationRule()
    {
    }

    public AssociationRule(
        Guid tenantId,
        Guid? branchId,
        string pairKey,
        string antecedentProductIds,
        string consequentProductIds,
        string antecedentNames,
        string consequentNames,
        double support,
        double confidence,
        double lift,
        int transactionCount,
        DateTime minedAtUtc)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        BranchId = branchId;
        PairKey = pairKey;
        AntecedentProductIds = antecedentProductIds;
        ConsequentProductIds = consequentProductIds;
        AntecedentNames = antecedentNames;
        ConsequentNames = consequentNames;
        Support = support;
        Confidence = confidence;
        Lift = lift;
        TransactionCount = transactionCount;
        MinedAtUtc = minedAtUtc;
        VoucherCreated = false;
    }

    public Guid TenantId { get; private set; }

    public Guid? BranchId { get; private set; }

    /// <summary>Stable hash of antecedent|consequent product id sets for idempotent upserts.</summary>
    public string PairKey { get; private set; } = string.Empty;

    public string AntecedentProductIds { get; private set; } = string.Empty;

    public string ConsequentProductIds { get; private set; } = string.Empty;

    public string AntecedentNames { get; private set; } = string.Empty;

    public string ConsequentNames { get; private set; } = string.Empty;

    public double Support { get; private set; }

    public double Confidence { get; private set; }

    public double Lift { get; private set; }

    public int TransactionCount { get; private set; }

    public DateTime MinedAtUtc { get; private set; }

    public bool VoucherCreated { get; private set; }

    public void Refresh(string antecedentNames, string consequentNames, double support, double confidence, double lift, int transactionCount, DateTime minedAtUtc)
    {
        AntecedentNames = antecedentNames;
        ConsequentNames = consequentNames;
        Support = support;
        Confidence = confidence;
        Lift = lift;
        TransactionCount = transactionCount;
        MinedAtUtc = minedAtUtc;
    }

    public void MarkVoucherCreated() => VoucherCreated = true;
}
