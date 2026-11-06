namespace AICOS.Contracts.Events;

/// <summary>Operational delivery confirmation, independent of payment and legacy order completion.</summary>
public sealed record OrderDeliveredIntegrationEvent(
    Guid TenantId, Guid BranchId, Guid OrderId, Guid? TableSessionId,
    Guid? DeliveredBy, DateTime DeliveredAtUtc) : IntegrationEvent;
