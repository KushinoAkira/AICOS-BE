using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.IdentityOrganization.Api.Domain;
using AICOS.IdentityOrganization.Api.Persistence;

namespace AICOS.IdentityOrganization.Api.Infrastructure;

/// <summary>
/// Writes audit entries (FR-67, BR-56). <see cref="Record"/> only adds the entry to the DbContext: the caller's own
/// SaveChanges stores it in the same transaction as the change it describes, so a change is never saved without its audit line.
/// </summary>
public sealed class AuditLogger(IdentityOrganizationDbContext db, ICurrentUser currentUser, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <param name="scope">Platform entries are for the SuperAdmin, Tenant entries for that brand's BrandAdmin.</param>
    /// <param name="before">Snapshot object (serialised to JSON) or null. Build it with <see cref="AuditSnapshots"/>.</param>
    public void Record(
        AuditScope scope, Guid? tenantId, string action, string entityType, Guid? entityId, object? before = null, object? after = null)
    {
        db.AuditLogs.Add(new AuditLog(
            scope,
            tenantId,
            currentUser.UserId,
            currentUser.Name,
            currentUser.Role,
            action,
            entityType,
            entityId?.ToString(),
            before is null ? null : JsonSerializer.Serialize(before, Json),
            after is null ? null : JsonSerializer.Serialize(after, Json),
            clock.GetUtcNow().UtcDateTime));
    }

    /// <summary>Entry about an account: Tenant scope for brand accounts, Platform scope for the SuperAdmin.</summary>
    public void RecordUser(string action, User user, object? before = null, object? after = null) =>
        Record(user.TenantId is null ? AuditScope.Platform : AuditScope.Tenant, user.TenantId, action, "User", user.Id, before, after);
}

/// <summary>Explicit field lists for audit snapshots, so secrets (password hash, tokens) can never leak into the history.</summary>
public static class AuditSnapshots
{
    public static object Of(User u) => new
    {
        u.FullName, u.Email, u.Username, u.Phone, u.Role, u.BranchId, Status = u.Status.ToString(),
    };

    public static object Of(Branch b) => new
    {
        b.Name, b.Address, b.Phone, b.OpeningTime, b.ClosingTime, Status = b.Status.ToString(),
    };

    public static object Of(Tenant t) => new { t.Name, t.Code, Status = t.Status.ToString() };
}
