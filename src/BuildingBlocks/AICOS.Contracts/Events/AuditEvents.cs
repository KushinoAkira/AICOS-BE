namespace AICOS.Contracts.Events;

/// <summary>
/// Published by any service for a sensitive change that must be kept in the audit history (FR-67, BR-56):
/// refunds, points/vouchers, attendance, shift replacement, payroll adjustment...
/// Consumed by Identity Service, which owns the append-only audit store. <see cref="Before"/> / <see cref="After"/> are
/// JSON snapshots and must never contain secrets or personal data beyond what the change needs.
/// </summary>
public sealed record AuditRecordedEvent(
    Guid? TenantId,
    Guid? ActorUserId,
    string? ActorName,
    string? ActorRole,
    string Action,
    string EntityType,
    string? EntityId,
    string? Before,
    string? After) : IntegrationEvent;
