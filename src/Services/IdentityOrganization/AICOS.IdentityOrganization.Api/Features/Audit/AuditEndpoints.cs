using System.Text.Json;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Persistence;
using AICOS.BuildingBlocks.Security;
using AICOS.BuildingBlocks.Validation;
using AICOS.Contracts.Events;
using AICOS.IdentityOrganization.Api.Domain;
using AICOS.IdentityOrganization.Api.Features.Common;
using AICOS.IdentityOrganization.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.IdentityOrganization.Api.Features.Audit;

public sealed record AuditLogResponse(
    Guid Id,
    AuditScope Scope,
    Guid? TenantId,
    Guid? ActorUserId,
    string? ActorName,
    string? ActorRole,
    string Action,
    string EntityType,
    string? EntityId,
    JsonElement? Before,
    JsonElement? After,
    DateTime OccurredAtUtc)
{
    public static AuditLogResponse From(AuditLog a) => new(
        a.Id, a.Scope, a.TenantId, a.ActorUserId, a.ActorName, a.ActorRole, a.Action, a.EntityType, a.EntityId,
        Parse(a.Before), Parse(a.After), a.OccurredAtUtc);

    private static JsonElement? Parse(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonDocument.Parse(json).RootElement.Clone();
}

/// <summary>
/// UC-11 View Audit Log. Routes: /api/v1/identity/audit-logs. Read-only by design (BR-56, NFR-12): there is no
/// create, update or delete endpoint. BrandAdmin reads the business history of their own brand; the SuperAdmin reads
/// platform-level history only and never a brand's business data (BR-06).
/// </summary>
public static class AuditEndpoints
{
    public static void MapAuditEndpoints(this RouteGroupBuilder api)
    {
        var audit = api.MapGroup("/audit-logs").WithTags("Audit").RequireAuthorization(AicosPolicies.AuditViewer);

        audit.MapGet("/", ListAsync);
        audit.MapGet("/{id:guid}", GetAsync);
    }

    /// <summary>The entries the caller is allowed to see: Platform scope for the SuperAdmin, own-tenant Tenant scope for a BrandAdmin.</summary>
    private static IQueryable<AuditLog> Visible(IdentityOrganizationDbContext db, ICurrentUser user)
    {
        if (user.IsInRole(AicosRoles.SuperAdmin))
        {
            return db.AuditLogs.Where(a => a.Scope == AuditScope.Platform);
        }

        var tenantId = Tenancy.Require(user);
        return db.AuditLogs.Where(a => a.Scope == AuditScope.Tenant && a.TenantId == tenantId);
    }

    private static async Task<PagedResponse<AuditLogResponse>> ListAsync(
        IdentityOrganizationDbContext db,
        ICurrentUser currentUser,
        DateTime? from,
        DateTime? to,
        string? action,
        string? entityType,
        string? entityId,
        Guid? actorUserId,
        int page = 1,
        int pageSize = 20,
        CancellationToken ct = default)
    {
        if (from is not null && to is not null && from > to)
        {
            throw new ValidationFailedException(nameof(from), "'from' must not be later than 'to'.");
        }

        var query = Visible(db, currentUser).AsNoTracking();

        if (from is not null)
        {
            query = query.Where(a => a.OccurredAtUtc >= from.Value.ToUniversalTime());
        }

        if (to is not null)
        {
            query = query.Where(a => a.OccurredAtUtc <= to.Value.ToUniversalTime());
        }

        if (!string.IsNullOrWhiteSpace(action))
        {
            // "user" matches every user.* action; "user.created" matches exactly one.
            var prefix = action.Trim().ToLowerInvariant();
            query = query.Where(a => a.Action == prefix || a.Action.StartsWith(prefix + "."));
        }

        if (!string.IsNullOrWhiteSpace(entityType))
        {
            var type = entityType.Trim();
            query = query.Where(a => a.EntityType == type);
        }

        if (!string.IsNullOrWhiteSpace(entityId))
        {
            var id = entityId.Trim();
            query = query.Where(a => a.EntityId == id);
        }

        if (actorUserId is not null)
        {
            query = query.Where(a => a.ActorUserId == actorUserId);
        }

        return await query.OrderByDescending(a => a.OccurredAtUtc).ToPagedAsync(page, pageSize, AuditLogResponse.From, ct);
    }

    private static async Task<AuditLogResponse> GetAsync(Guid id, IdentityOrganizationDbContext db, ICurrentUser currentUser, CancellationToken ct) =>
        AuditLogResponse.From(
            await Visible(db, currentUser).AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct)
            ?? throw new NotFoundException("Audit log entry", id));
}

/// <summary>
/// Other services -> Identity: store an audit entry for a sensitive change they made (refund, points, attendance...).
/// Idempotent on the event id, so a redelivered event does not create a duplicate line.
/// </summary>
public sealed class AuditRecordedHandler(IdentityOrganizationDbContext db, ILogger<AuditRecordedHandler> logger)
    : IIntegrationEventHandler<AuditRecordedEvent>
{
    public async Task HandleAsync(AuditRecordedEvent @event, CancellationToken cancellationToken)
    {
        if (await db.AuditLogs.AnyAsync(a => a.SourceEventId == @event.EventId, cancellationToken))
        {
            return;
        }

        var scope = @event.TenantId is null ? AuditScope.Platform : AuditScope.Tenant;
        db.AuditLogs.Add(new AuditLog(
            scope, @event.TenantId, @event.ActorUserId, @event.ActorName, @event.ActorRole, @event.Action,
            @event.EntityType, @event.EntityId, @event.Before, @event.After, @event.OccurredOnUtc, @event.EventId));
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Stored audit entry {Action} for {EntityType} {EntityId}", @event.Action, @event.EntityType, @event.EntityId);
    }
}
