using AICOS.BuildingBlocks.Validation;
using AICOS.IdentityOrganization.Api.Domain;
using AICOS.IdentityOrganization.Api.Features.Common;

namespace AICOS.IdentityOrganization.Api.Features.Users;

public sealed record UserResponse(
    Guid Id,
    string FullName,
    string Email,
    string? Username,
    string? Phone,
    string Role,
    Guid? BranchId,
    UserStatus Status,
    bool MustChangePassword,
    DateTime? LockoutEndUtc,
    DateTime? LastLoginAtUtc,
    DateTime CreatedAtUtc,
    DateTime? UpdatedAtUtc)
{
    public static UserResponse From(User user) => new(
        user.Id,
        user.FullName,
        user.Email,
        user.Username,
        user.Phone,
        user.Role,
        user.BranchId,
        user.Status,
        user.MustChangePassword,
        user.LockoutEndUtc,
        user.LastLoginAtUtc,
        user.CreatedAtUtc,
        user.UpdatedAtUtc);
}

/// <summary>POST /users - an admin creates an account with a temporary password (the user must change it).</summary>
public sealed record CreateUserRequest(
    string? FullName, string? Email, string? Username, string? Phone, string? Password, string? Role, Guid? BranchId);

/// <summary>PUT /users/{id}. Password is not changed here (use forgot/change password).</summary>
public sealed record UpdateUserRequest(
    string? FullName, string? Email, string? Username, string? Phone, string? Role, Guid? BranchId);

public sealed record ChangeUserStatusRequest(UserStatus? Status);

internal static class UserRequestValidator
{
    public static void EnsureValid(CreateUserRequest request)
    {
        var errors = Validate(request.FullName, request.Email, request.Username, request.Phone, request.Role, request.BranchId);
        InputRules.NewPassword(errors, nameof(request.Password), request.Password, nameof(request.Password), request.Password);
        errors.ThrowIfAny();
    }

    public static void EnsureValid(UpdateUserRequest request) =>
        Validate(request.FullName, request.Email, request.Username, request.Phone, request.Role, request.BranchId).ThrowIfAny();

    /// <summary>Returns the role spelled exactly as in <see cref="User.AssignableRoles"/>, or null.</summary>
    public static string? CanonicalRole(string? role) =>
        User.AssignableRoles.FirstOrDefault(r => string.Equals(r, role?.Trim(), StringComparison.OrdinalIgnoreCase));

    private static ValidationErrors Validate(string? fullName, string? email, string? username, string? phone, string? role, Guid? branchId)
    {
        var errors = new ValidationErrors();
        CommonRules.Required(errors, "FullName", fullName, "Full name", User.FullNameMaxLength);
        CommonRules.Email(errors, "Email", email);
        InputRules.Username(errors, "Username", username);
        CommonRules.Phone(errors, "Phone", phone);

        var canonicalRole = CanonicalRole(role);
        if (canonicalRole is null)
        {
            errors.Add("Role", $"Role must be one of: {string.Join(", ", User.AssignableRoles)}.");
        }
        else if (User.BranchRoles.Contains(canonicalRole) && branchId is null)
        {
            errors.Add("BranchId", $"BranchId is required for role {canonicalRole}.");
        }
        else if (!User.BranchRoles.Contains(canonicalRole) && branchId is not null)
        {
            errors.Add("BranchId", $"Role {canonicalRole} must not be assigned to a branch.");
        }

        return errors;
    }
}
