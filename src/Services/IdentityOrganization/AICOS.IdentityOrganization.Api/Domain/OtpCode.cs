using AICOS.BuildingBlocks.Domain;

namespace AICOS.IdentityOrganization.Api.Domain;

public static class OtpPurposes
{
    public const string PasswordReset = "password_reset";
    public const string EmailVerification = "email_verification";
}

/// <summary>One-time code sent by email (forgot password, sign-up verification). Only the SHA-256 hash of the code is stored.</summary>
public sealed class OtpCode : Entity<Guid>
{
    private OtpCode()
    {
    }

    public OtpCode(string target, string codeHash, string purpose, DateTime nowUtc, DateTime expiresAtUtc)
    {
        Id = Guid.NewGuid();
        Target = target;
        CodeHash = codeHash;
        Purpose = purpose;
        CreatedAtUtc = nowUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    /// <summary>Normalized email the code was sent to.</summary>
    public string Target { get; private set; } = string.Empty;

    public string CodeHash { get; private set; } = string.Empty;

    public string Purpose { get; private set; } = string.Empty;

    public int Attempts { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public DateTime ExpiresAtUtc { get; private set; }

    public DateTime? ConsumedAtUtc { get; private set; }

    public bool IsUsable(DateTime nowUtc, int maxAttempts) =>
        ConsumedAtUtc is null && ExpiresAtUtc > nowUtc && Attempts < maxAttempts;

    public void RecordFailedAttempt() => Attempts++;

    public void Consume(DateTime nowUtc) => ConsumedAtUtc ??= nowUtc;
}
