namespace AICOS.Contracts.Events;

/// <summary>
/// Ordering mined an Apriori rule strong enough for a combo promotion. Customer Engagement may issue a voucher (idempotent by PairKey).
/// </summary>
public sealed record AssociationRulePromotedEvent(
    Guid TenantId,
    Guid? BranchId,
    Guid RuleId,
    string PairKey,
    string AntecedentNames,
    string ConsequentNames,
    double Support,
    double Confidence,
    double Lift,
    int TransactionCount) : IntegrationEvent;
