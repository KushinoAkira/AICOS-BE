using AICOS.BuildingBlocks.Domain;
using AICOS.BuildingBlocks.Security;

namespace AICOS.IdentityOrganization.Api.Domain;

public enum UserStatus
{
    Active,

    /// <summary>Locked by a SystemAdmin (UC-02). Only an admin can unlock it.</summary>
    Locked,
}

/// <summary>
/// A login account (UC-01, UC-02, UC-33). BR-01: exactly one role per account.
/// Staff roles (Manager, Cashier, WarehouseStaff) belong to exactly one branch; SystemAdmin and Customer to none.
/// </summary>
public sealed class User : AuditableEntity<Guid>
{
    public const int FullNameMaxLength = 255;
    public const int EmailMaxLength = 255;
    public const int UsernameMaxLength = 50;
    public const int PhoneMaxLength = 20;

    /// <summary>Roles an admin may assign. Guest is anonymous and AiBusinessAssistant is not a human login.</summary>
    public static readonly IReadOnlyList<string> AssignableRoles =
        [AicosRoles.SystemAdmin, AicosRoles.Manager, AicosRoles.Cashier, AicosRoles.WarehouseStaff, AicosRoles.Customer];

    /// <summary>Roles that work inside one branch and therefore require a BranchId.</summary>
    public static readonly IReadOnlyList<string> BranchRoles =
        [AicosRoles.Manager, AicosRoles.Cashier, AicosRoles.WarehouseStaff];

    private User()
    {
    }

    public User(string fullName, string email, string? username, string? phone, string role, Guid? branchId)
    {
        Id = Guid.NewGuid();
        Status = UserStatus.Active;
        UpdateProfile(fullName, email, username, phone);
        AssignRole(role, branchId);
    }

    public string FullName { get; private set; } = string.Empty;

    /// <summary>Stored trimmed and lower-cased.</summary>
    public string Email { get; private set; } = string.Empty;

    /// <summary>Optional second login name, stored lower-cased.</summary>
    public string? Username { get; private set; }

    public string? Phone { get; private set; }

    public string PasswordHash { get; private set; } = string.Empty;

    public string Role { get; private set; } = string.Empty;

    public Guid? BranchId { get; private set; }

    public UserStatus Status { get; private set; }

    /// <summary>True for accounts created by an admin with a temporary password (client should force a change).</summary>
    public bool MustChangePassword { get; private set; }

    public int FailedLoginCount { get; private set; }

    /// <summary>BR-02: set after too many consecutive failed logins; login is refused until this time.</summary>
    public DateTime? LockoutEndUtc { get; private set; }

    public DateTime? LastLoginAtUtc { get; private set; }

    /// <summary>Soft delete: the row is kept for audit/references from other services, but hidden and unable to log in.</summary>
    public DateTime? DeletedAtUtc { get; private set; }

    public static string NormalizeLogin(string value) => value.Trim().ToLowerInvariant();

    public void UpdateProfile(string fullName, string email, string? username, string? phone)
    {
        FullName = fullName.Trim();
        Email = NormalizeLogin(email);
        Username = string.IsNullOrWhiteSpace(username) ? null : NormalizeLogin(username);
        Phone = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim();
    }

    public void AssignRole(string role, Guid? branchId)
    {
        Role = role;
        BranchId = branchId;
    }

    public void SetPassword(string passwordHash, bool mustChangePassword)
    {
        PasswordHash = passwordHash;
        MustChangePassword = mustChangePassword;
    }

    public bool IsLockedOut(DateTime nowUtc) => LockoutEndUtc > nowUtc;

    /// <returns>True when this failure triggered the temporary lockout.</returns>
    public bool RecordFailedLogin(DateTime nowUtc, int maxAttempts, TimeSpan lockoutDuration)
    {
        FailedLoginCount++;
        if (FailedLoginCount < maxAttempts)
        {
            return false;
        }

        FailedLoginCount = 0;
        LockoutEndUtc = nowUtc.Add(lockoutDuration);
        return true;
    }

    public void RecordSuccessfulLogin(DateTime nowUtc)
    {
        FailedLoginCount = 0;
        LockoutEndUtc = null;
        LastLoginAtUtc = nowUtc;
    }

    public void ClearLockout()
    {
        FailedLoginCount = 0;
        LockoutEndUtc = null;
    }

    public void ChangeStatus(UserStatus status)
    {
        Status = status;
        if (status == UserStatus.Active)
        {
            ClearLockout();
        }
    }

    public void MarkDeleted(DateTime nowUtc) => DeletedAtUtc = nowUtc;
}
