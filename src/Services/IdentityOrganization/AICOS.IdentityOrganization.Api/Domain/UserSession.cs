using AICOS.BuildingBlocks.Domain;

namespace AICOS.IdentityOrganization.Api.Domain;

/// <summary>
/// One signed-in device. Holds the SHA-256 hash of the current refresh token (never the token itself).
/// The token is rotated on every refresh; logout / password change / lock revokes the session.
/// </summary>
public sealed class UserSession : Entity<Guid>
{
    public const int DeviceLabelMaxLength = 255;
    public const int IpAddressMaxLength = 50;

    private UserSession()
    {
    }

    public UserSession(Guid userId, string refreshTokenHash, string? deviceLabel, string? ipAddress, DateTime nowUtc, DateTime expiresAtUtc)
    {
        Id = Guid.NewGuid();
        UserId = userId;
        RefreshTokenHash = refreshTokenHash;
        DeviceLabel = Truncate(deviceLabel, DeviceLabelMaxLength);
        IpAddress = Truncate(ipAddress, IpAddressMaxLength);
        CreatedAtUtc = nowUtc;
        LastActiveAtUtc = nowUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    public Guid UserId { get; private set; }

    public User? User { get; private set; }

    public string RefreshTokenHash { get; private set; } = string.Empty;

    public string? DeviceLabel { get; private set; }

    public string? IpAddress { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public DateTime LastActiveAtUtc { get; private set; }

    public DateTime ExpiresAtUtc { get; private set; }

    public DateTime? RevokedAtUtc { get; private set; }

    public bool IsActive(DateTime nowUtc) => RevokedAtUtc is null && ExpiresAtUtc > nowUtc;

    public void Rotate(string newRefreshTokenHash, DateTime nowUtc, DateTime expiresAtUtc)
    {
        RefreshTokenHash = newRefreshTokenHash;
        LastActiveAtUtc = nowUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    public void Revoke(DateTime nowUtc) => RevokedAtUtc ??= nowUtc;

    private static string? Truncate(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= maxLength ? value : value[..maxLength];
}
