using AICOS.BuildingBlocks.Domain;

namespace AICOS.CustomerEngagement.Api.Domain;

/// <summary>
/// A message for everyone holding one role, in one brand and possibly one branch. <see cref="TenantId"/> is null for the Super Admin.
/// One event makes one row per role; <see cref="DedupeKey"/> keeps a repeated event from making a second one. Who read it is kept per person
/// in <see cref="StaffNotificationRead"/>, because the row is shared.
/// </summary>
public sealed class StaffNotification : Entity<Guid>
{
    private StaffNotification()
    {
    }

    public StaffNotification(Guid? tenantId, Guid? branchId, string audience, string kind, string dedupeKey, string title, string body, DateTime createdAtUtc)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        BranchId = branchId;
        Audience = audience;
        Kind = kind;
        DedupeKey = dedupeKey;
        Title = title;
        Body = body;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid? TenantId { get; private set; }

    /// <summary>Null when it concerns the whole brand.</summary>
    public Guid? BranchId { get; private set; }

    /// <summary>The role it is for.</summary>
    public string Audience { get; private set; } = string.Empty;

    public string Kind { get; private set; } = string.Empty;

    public string DedupeKey { get; private set; } = string.Empty;

    public string Title { get; private set; } = string.Empty;

    public string Body { get; private set; } = string.Empty;

    public DateTime CreatedAtUtc { get; private set; }
}

/// <summary>One person has read one <see cref="StaffNotification"/>.</summary>
public sealed class StaffNotificationRead
{
    private StaffNotificationRead()
    {
    }

    public StaffNotificationRead(Guid notificationId, Guid userId, DateTime readAtUtc)
    {
        NotificationId = notificationId;
        UserId = userId;
        ReadAtUtc = readAtUtc;
    }

    public Guid NotificationId { get; private set; }

    public Guid UserId { get; private set; }

    public DateTime ReadAtUtc { get; private set; }
}
