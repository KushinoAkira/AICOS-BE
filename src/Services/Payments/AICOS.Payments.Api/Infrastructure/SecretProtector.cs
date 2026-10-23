using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace AICOS.Payments.Api.Infrastructure;

/// <summary>Payment service settings. Section "Payments" in appsettings.</summary>
public sealed class PaymentOptions
{
    public const string SectionName = "Payments";

    /// <summary>32 random bytes, base64. Encrypts the credentials of payment providers. Keep it out of the repository (NFR-06).</summary>
    public string? SecretKey { get; set; }

    /// <summary>Key that signs webhooks of the Mock provider (providers with credentials use their own secret key).</summary>
    public string MockWebhookSecret { get; set; } = "aicos-mock-webhook-secret";

    /// <summary>How often the background job expires unpaid payments.</summary>
    public int ExpirySweepSeconds { get; set; } = 60;

    /// <summary>Lets staff and customers fake the provider result (Mock and sandbox demos). Off in Production unless set.</summary>
    public bool? AllowSimulation { get; set; }

    /// <summary>IANA time zone used to turn report dates into days.</summary>
    public string TimeZone { get; set; } = "Asia/Ho_Chi_Minh";
}

/// <summary>
/// Encrypts provider credentials before they reach the database (AES-256-GCM, a fresh nonce per value).
/// The key comes from configuration, so the encrypted values survive a container restart or a redeploy.
/// </summary>
public sealed class SecretProtector(IOptions<PaymentOptions> options)
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private byte[]? _key;

    public string Protect(string plainText)
    {
        var plain = Encoding.UTF8.GetBytes(plainText);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(Key(), TagSize);
        aes.Encrypt(nonce, plain, cipher, tag);

        return Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    public string Unprotect(string protectedText)
    {
        var data = Convert.FromBase64String(protectedText);
        var nonce = data[..NonceSize];
        var tag = data[NonceSize..(NonceSize + TagSize)];
        var cipher = data[(NonceSize + TagSize)..];
        var plain = new byte[cipher.Length];

        using var aes = new AesGcm(Key(), TagSize);
        aes.Decrypt(nonce, cipher, tag, plain);

        return Encoding.UTF8.GetString(plain);
    }

    private byte[] Key()
    {
        if (_key is not null)
        {
            return _key;
        }

        try
        {
            var key = Convert.FromBase64String(options.Value.SecretKey ?? string.Empty);
            if (key.Length == 32)
            {
                return _key = key;
            }
        }
        catch (FormatException)
        {
            // falls through to the message below
        }

        throw new InvalidOperationException(
            "Payments:SecretKey must be 32 random bytes encoded as base64 (for example `openssl rand -base64 32`). Credentials cannot be stored without it.");
    }
}
