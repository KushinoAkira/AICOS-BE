namespace AICOS.IdentityOrganization.Api.Domain;

public static class IdentityStatuses
{
    public const string Active = "ACTIVE";
    public const string Inactive = "INACTIVE";
}

public sealed class UserAccount
{
    private UserAccount()
    {
    }

    public UserAccount(Guid id, string email, string fullName, string status = IdentityStatuses.Active)
    {
        Id = id;
        Email = email.Trim().ToLowerInvariant();
        FullName = fullName.Trim();
        Status = status;
    }

    public Guid Id { get; private set; }
    public string Email { get; private set; } = string.Empty;
    public string? PasswordHash { get; private set; }
    public string FullName { get; private set; } = string.Empty;
    public string? Phone { get; private set; }
    public string? Status { get; private set; } = IdentityStatuses.Active;
    public DateTime? CreatedAt { get; private set; }

    public void SetPasswordHash(string passwordHash) =>
        PasswordHash = string.IsNullOrWhiteSpace(passwordHash)
            ? throw new ArgumentException("Password hash is required.", nameof(passwordHash))
            : passwordHash;
}

public sealed class Tenant
{
    private Tenant()
    {
    }

    public Tenant(Guid id, string name, string code, string status = IdentityStatuses.Active)
    {
        Id = id;
        Name = name.Trim();
        Code = code.Trim();
        Status = status;
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Code { get; private set; } = string.Empty;
    public string Status { get; private set; } = IdentityStatuses.Active;
    public DateTime? CreatedAt { get; private set; }
    public DateTime? UpdatedAt { get; private set; }
}

public sealed class Branch
{
    private Branch()
    {
    }

    public Branch(Guid id, Guid tenantId, string code, string name, string status = IdentityStatuses.Inactive)
    {
        Id = id;
        TenantId = tenantId;
        Code = code.Trim();
        Name = name.Trim();
        Status = status;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string? Address { get; private set; }
    public string? Phone { get; private set; }
    public string? Status { get; private set; } = IdentityStatuses.Inactive;
    public DateTime? CreatedAt { get; private set; }
}

public sealed class Role
{
    private Role()
    {
    }

    public Role(Guid id, string code, string name)
    {
        Id = id;
        Code = code.Trim();
        Name = name.Trim();
    }

    public Guid Id { get; private set; }
    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
}

public sealed class TenantUser
{
    private TenantUser()
    {
    }

    public TenantUser(Guid id, Guid tenantId, Guid userId, Guid roleId, string status = IdentityStatuses.Active)
    {
        Id = id;
        TenantId = tenantId;
        UserId = userId;
        RoleId = roleId;
        Status = status;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid UserId { get; private set; }
    public Guid RoleId { get; private set; }
    public string? Status { get; private set; } = IdentityStatuses.Active;
    public UserAccount User { get; private set; } = null!;
    public Role Role { get; private set; } = null!;
}

public sealed class StaffAssignment
{
    private StaffAssignment()
    {
    }

    public StaffAssignment(Guid id, Guid branchId, Guid tenantUserId, string status = IdentityStatuses.Active)
    {
        Id = id;
        BranchId = branchId;
        TenantUserId = tenantUserId;
        Status = status;
    }

    public Guid Id { get; private set; }
    public Guid BranchId { get; private set; }
    public Guid TenantUserId { get; private set; }
    public string? Status { get; private set; } = IdentityStatuses.Active;
    public Branch Branch { get; private set; } = null!;
    public TenantUser TenantUser { get; private set; } = null!;
}

public sealed class RefreshSession
{
    private RefreshSession()
    {
    }

    public RefreshSession(
        Guid id,
        Guid userId,
        Guid branchId,
        string tokenHash,
        DateTime expiresAtUtc,
        DateTime createdAtUtc)
    {
        Id = id;
        UserId = userId;
        BranchId = branchId;
        TokenHash = tokenHash;
        ExpiresAtUtc = expiresAtUtc;
        CreatedAtUtc = createdAtUtc;
        Version = Guid.NewGuid();
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public Guid BranchId { get; private set; }
    public string TokenHash { get; private set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? RevokedAtUtc { get; private set; }
    public Guid? ReplacedBySessionId { get; private set; }
    public Guid Version { get; private set; }

    public bool IsActive(DateTime nowUtc) => RevokedAtUtc is null && ExpiresAtUtc > nowUtc;

    public void Revoke(DateTime revokedAtUtc, Guid? replacedBySessionId = null)
    {
        if (RevokedAtUtc is not null)
        {
            return;
        }

        RevokedAtUtc = revokedAtUtc;
        ReplacedBySessionId = replacedBySessionId;
        Version = Guid.NewGuid();
    }
}
