using System.ComponentModel.DataAnnotations;

namespace AICOS.IdentityOrganization.Api.Infrastructure;

/// <summary>Login / session / password-reset settings. Section "Auth" in appsettings.</summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    [Range(1, 90)]
    public int RefreshTokenDays { get; set; } = 7;

    /// <summary>BR-02: consecutive failed logins before the account is temporarily locked.</summary>
    [Range(1, 20)]
    public int MaxFailedLoginAttempts { get; set; } = 5;

    [Range(1, 1440)]
    public int LockoutMinutes { get; set; } = 15;

    [Range(1, 60)]
    public int PasswordResetCodeMinutes { get; set; } = 15;

    [Range(1, 10)]
    public int PasswordResetMaxAttempts { get; set; } = 5;

    /// <summary>Minimum delay between two reset emails to the same address (anti-spam).</summary>
    [Range(0, 3600)]
    public int PasswordResetResendSeconds { get; set; } = 60;

    /// <summary>Sign-up: minutes the emailed verification code stays valid.</summary>
    [Range(1, 60)]
    public int SignUpCodeMinutes { get; set; } = 10;

    [Range(1, 10)]
    public int SignUpMaxAttempts { get; set; } = 5;

    /// <summary>Minimum delay between two verification emails to the same address.</summary>
    [Range(0, 3600)]
    public int SignUpResendSeconds { get; set; } = 60;

    /// <summary>Platform SuperAdmin, created at start-up when none exists. Leave Email empty to disable.</summary>
    public SeedAdminOptions SeedAdmin { get; set; } = new();

    public sealed class SeedAdminOptions
    {
        public string? Email { get; set; }

        public string? Password { get; set; }

        public string FullName { get; set; } = "System Administrator";
    }
}
