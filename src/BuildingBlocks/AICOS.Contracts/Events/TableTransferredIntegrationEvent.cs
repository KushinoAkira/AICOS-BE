namespace AICOS.Contracts.Events;

public sealed record TableTransferredIntegrationEvent(Guid TenantId, Guid BranchId, Guid SessionId,
    Guid TableId, string TableCode, IReadOnlyList<Guid> OrderIds) : IntegrationEvent;