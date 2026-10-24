using AICOS.BuildingBlocks.Domain;

namespace AICOS.IdentityOrganization.Api.Domain;

/// <summary>Who may read an audit entry: the platform owner or the brand it belongs to (BR-06).</summary>
public enum AuditScope
{
    /// <summary>Platform-level changes (tenants, plans). Visible to the SuperAdmin only.</summary>
    Platform,

    /// <summary>Business changes inside one brand. Visible to that brand's BrandAdmin only.</summary>
    Tenant,
}

/// <summary>
/// One immutable line of the audit history (FR-67, BR-56, NFR-12): who did what to which record, when, with the
/// before/after values. Append-only: there is no update or delete anywhere - not in the API, not in EF, not in the database.
/// </summary>
public sealed class AuditLog : Entity<Guid>
{
    public const int ActionMaxLength = 80;
    public const int EntityTypeMaxLength = 50;
    public const int EntityIdMaxLength = 100;
    public const int ActorNameMaxLength = 255;

    private AuditLog()
    {
    }

    public AuditLog(
        AuditScope scope,
        Guid? tenantId,
        Guid? actorUserId,
        string? actorName,
        string? actorRole,
        string action,
        string entityType,
        string? entityId,
        string? before,
        string? after,
        DateTime occurredAtUtc,
        Guid? sourceEventId = null)
    {
        Id = Guid.NewGuid();
        Scope = scope;
        TenantId = tenantId;
        ActorUserId = actorUserId;
        ActorName = Trim(actorName, ActorNameMaxLength);
        ActorRole = actorRole;
        Action = Trim(action, ActionMaxLength)!;
        EntityType = Trim(entityType, EntityTypeMaxLength)!;
        EntityId = Trim(entityId, EntityIdMaxLength);
        Before = before;
        After = after;
        OccurredAtUtc = occurredAtUtc;
        SourceEventId = sourceEventId;
    }

    public AuditScope Scope { get; private set; }

    public Guid? TenantId { get; private set; }

    /// <summary>Null when the system acted on its own (e.g. automatic lockout after failed logins).</summary>
    public Guid? ActorUserId { get; private set; }

    public string? ActorName { get; private set; }

    public string? ActorRole { get; private set; }

    /// <summary>Dotted verb, e.g. "user.status_changed", "branch.created".</summary>
    public string Action { get; private set; } = string.Empty;

    public string EntityType { get; private set; } = string.Empty;

    public string? EntityId { get; private set; }

    /// <summary>JSON snapshot before the change. Never contains passwords or tokens.</summary>
    public string? Before { get; private set; }

    public string? After { get; private set; }

    public DateTime OccurredAtUtc { get; private set; }

    /// <summary>Set for entries received from other services; makes handling a redelivered event idempotent.</summary>
    public Guid? SourceEventId { get; private set; }

    private static string? Trim(string? value, int max)
    {
        var trimmed = value?.Trim();
        return trimmed is { Length: > 0 } && trimmed.Length > max ? trimmed[..max] : trimmed;
    }
}
