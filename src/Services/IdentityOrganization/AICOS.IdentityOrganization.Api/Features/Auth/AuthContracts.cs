using AICOS.BuildingBlocks.Validation;
using AICOS.IdentityOrganization.Api.Features.Common;
using AICOS.IdentityOrganization.Api.Features.Users;
using AICOS.IdentityOrganization.Api.Infrastructure;

namespace AICOS.IdentityOrganization.Api.Features.Auth;

/// <summary>UC-04: a guest registers a Customer account of one brand (BR-72). <see cref="TenantCode"/> is the brand identifier.</summary>
public sealed record RegisterRequest(
    string? TenantCode, string? FullName, string? Email, string? Phone, string? Password, string? ConfirmPassword);

/// <summary>UC-01: <see cref="Login"/> is the email OR the username.</summary>
public sealed record LoginRequest(string? Login, string? Password);

public sealed record RefreshTokenRequest(string? RefreshToken);

/// <summary>UC-03: PUT /auth/me. Email and role cannot be changed here; <see cref="Username"/> is optional.</summary>
public sealed record UpdateProfileRequest(string? FullName, string? Phone, string? Username);

public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword, string? ConfirmNewPassword);

public sealed record ForgotPasswordRequest(string? Email);

public sealed record ResetPasswordRequest(string? Email, string? Code, string? NewPassword, string? ConfirmNewPassword);

public sealed record AuthResponse(
    string AccessToken,
    DateTime AccessTokenExpiresAtUtc,
    string RefreshToken,
    DateTime RefreshTokenExpiresAtUtc,
    UserResponse User)
{
    public string TokenType => "Bearer";

    public static AuthResponse From(IssuedTokens tokens, UserResponse user) => new(
        tokens.AccessToken, tokens.AccessTokenExpiresAtUtc, tokens.RefreshToken, tokens.RefreshTokenExpiresAtUtc, user);
}

public sealed record MessageResponse(string Message);

internal static class AuthRequestValidator
{
    public static void EnsureValid(RegisterRequest r)
    {
        var errors = new ValidationErrors();
        CommonRules.Required(errors, nameof(r.TenantCode), r.TenantCode, "Brand code", Domain.Tenant.CodeMaxLength);
        CommonRules.Required(errors, nameof(r.FullName), r.FullName, "Full name", Domain.User.FullNameMaxLength);
        CommonRules.Email(errors, nameof(r.Email), r.Email);
        CommonRules.Phone(errors, nameof(r.Phone), r.Phone);
        InputRules.NewPassword(errors, nameof(r.Password), r.Password, nameof(r.ConfirmPassword), r.ConfirmPassword);
        errors.ThrowIfAny();
    }

    public static void EnsureValid(LoginRequest r)
    {
        var errors = new ValidationErrors();
        if (string.IsNullOrWhiteSpace(r.Login))
        {
            errors.Add(nameof(r.Login), "Email or username is required.");
        }

        if (string.IsNullOrEmpty(r.Password))
        {
            errors.Add(nameof(r.Password), "Password is required.");
        }

        errors.ThrowIfAny();
    }

    public static void EnsureValid(UpdateProfileRequest r)
    {
        var errors = new ValidationErrors();
        CommonRules.Required(errors, nameof(r.FullName), r.FullName, "Full name", Domain.User.FullNameMaxLength);
        CommonRules.Phone(errors, nameof(r.Phone), r.Phone);
        InputRules.Username(errors, nameof(r.Username), r.Username);
        errors.ThrowIfAny();
    }

    public static void EnsureValid(RefreshTokenRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.RefreshToken))
        {
            throw new BuildingBlocks.Exceptions.ValidationFailedException(nameof(r.RefreshToken), "Refresh token is required.");
        }
    }

    public static void EnsureValid(ChangePasswordRequest r)
    {
        var errors = new ValidationErrors();
        if (string.IsNullOrEmpty(r.CurrentPassword))
        {
            errors.Add(nameof(r.CurrentPassword), "Current password is required.");
        }

        InputRules.NewPassword(errors, nameof(r.NewPassword), r.NewPassword, nameof(r.ConfirmNewPassword), r.ConfirmNewPassword);
        if (!errors.Has(nameof(r.NewPassword)) && r.NewPassword == r.CurrentPassword)
        {
            errors.Add(nameof(r.NewPassword), "New password must be different from the current password.");
        }

        errors.ThrowIfAny();
    }

    public static void EnsureValid(ForgotPasswordRequest r)
    {
        var errors = new ValidationErrors();
        CommonRules.Email(errors, nameof(r.Email), r.Email);
        errors.ThrowIfAny();
    }

    public static void EnsureValid(ResetPasswordRequest r)
    {
        var errors = new ValidationErrors();
        CommonRules.Email(errors, nameof(r.Email), r.Email);
        if (string.IsNullOrWhiteSpace(r.Code) || r.Code.Trim().Length != 6 || !r.Code.Trim().All(char.IsAsciiDigit))
        {
            errors.Add(nameof(r.Code), "Code must be the 6-digit code from the email.");
        }

        InputRules.NewPassword(errors, nameof(r.NewPassword), r.NewPassword, nameof(r.ConfirmNewPassword), r.ConfirmNewPassword);
        errors.ThrowIfAny();
    }
}
