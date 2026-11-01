using AICOS.BuildingBlocks.Validation;
using AICOS.IdentityOrganization.Api.Features.Common;
using AICOS.IdentityOrganization.Api.Features.Users;
using AICOS.IdentityOrganization.Api.Infrastructure;

namespace AICOS.IdentityOrganization.Api.Features.Auth;

/// <summary>UC-04: a guest registers one platform-wide Customer account; it stays pending until the emailed code is verified.</summary>
public sealed record RegisterRequest(string? FullName, string? Email, string? Password, string? ConfirmPassword);

/// <summary>Sign-up step 2: the 6-digit code from the email.</summary>
public sealed record VerifyRegistrationRequest(string? Email, string? Code);

public sealed record ResendRegistrationRequest(string? Email);

/// <summary>Sign in / sign up with Google: <see cref="IdToken"/> is the credential Google Identity Services hands to the web app.</summary>
public sealed record GoogleLoginRequest(string? IdToken);

/// <summary>A signed-in customer picks the brand to order from, by code or (from a table QR) by id. <see cref="RefreshToken"/> names the session.</summary>
public sealed record SelectBrandRequest(string? RefreshToken, string? TenantCode, Guid? TenantId);

/// <summary>A verification code is on its way (or was sent moments ago). The client counts down <see cref="ResendAfterSeconds"/> before offering a resend.</summary>
public sealed record SignUpPendingResponse(string Email, int ExpiresInSeconds, int ResendAfterSeconds);

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
    UserResponse User,
    string? ActiveBrandCode = null)
{
    public string TokenType => "Bearer";

    public static AuthResponse From(IssuedTokens tokens, UserResponse user, string? activeBrandCode = null) => new(
        tokens.AccessToken, tokens.AccessTokenExpiresAtUtc, tokens.RefreshToken, tokens.RefreshTokenExpiresAtUtc, user, activeBrandCode);
}

public sealed record MessageResponse(string Message);

internal static class AuthRequestValidator
{
    public static void EnsureValid(RegisterRequest r)
    {
        var errors = new ValidationErrors();
        CommonRules.Required(errors, nameof(r.FullName), r.FullName, "Full name", Domain.User.FullNameMaxLength);
        CommonRules.Email(errors, nameof(r.Email), r.Email);
        InputRules.NewPassword(errors, nameof(r.Password), r.Password, nameof(r.ConfirmPassword), r.ConfirmPassword);
        errors.ThrowIfAny();
    }

    public static void EnsureValid(VerifyRegistrationRequest r)
    {
        var errors = new ValidationErrors();
        CommonRules.Email(errors, nameof(r.Email), r.Email);
        if (string.IsNullOrWhiteSpace(r.Code) || r.Code.Trim().Length != 6 || !r.Code.Trim().All(char.IsAsciiDigit))
        {
            errors.Add(nameof(r.Code), "Code must be the 6-digit code from the email.");
        }

        errors.ThrowIfAny();
    }

    public static void EnsureValid(ResendRegistrationRequest r)
    {
        var errors = new ValidationErrors();
        CommonRules.Email(errors, nameof(r.Email), r.Email);
        errors.ThrowIfAny();
    }

    public static void EnsureValid(GoogleLoginRequest r)
    {
        var errors = new ValidationErrors();
        if (string.IsNullOrWhiteSpace(r.IdToken))
        {
            errors.Add(nameof(r.IdToken), "Google credential is required.");
        }

        errors.ThrowIfAny();
    }

    public static void EnsureValid(SelectBrandRequest r)
    {
        var errors = new ValidationErrors();
        if (string.IsNullOrWhiteSpace(r.RefreshToken))
        {
            errors.Add(nameof(r.RefreshToken), "Refresh token is required.");
        }

        if (string.IsNullOrWhiteSpace(r.TenantCode) && r.TenantId is null)
        {
            errors.Add(nameof(r.TenantCode), "Choose a brand.");
        }

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
