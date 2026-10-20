using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;

namespace AICOS.BuildingBlocks.Messaging;

/// <summary>
/// Sends an audit entry for a sensitive change (FR-67, BR-56) to the Identity service, which stores the append-only history.
/// Call it after the change was saved. Publishing is best effort: a failure is logged and never fails the change itself.
/// </summary>
public static class AuditTrail
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <param name="tenantId">The brand the change belongs to; null for platform-level changes (visible to the SuperAdmin only).</param>
    /// <param name="before">Snapshot object before the change, or null. Never put secrets or personal data in a snapshot.</param>
    public static async Task PublishAsync(
        IEventBus bus,
        ICurrentUser user,
        ILogger logger,
        Guid? tenantId,
        string action,
        string entityType,
        object entityId,
        object? before = null,
        object? after = null)
    {
        try
        {
            await bus.PublishAsync(new AuditRecordedEvent(
                tenantId,
                user.UserId,
                user.Name,
                user.Role,
                action,
                entityType,
                entityId.ToString(),
                before is null ? null : JsonSerializer.Serialize(before, Json),
                after is null ? null : JsonSerializer.Serialize(after, Json)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not publish the audit event {Action}", action);
        }
    }
}
