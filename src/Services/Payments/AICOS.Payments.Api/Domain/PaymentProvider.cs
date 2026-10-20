using AICOS.BuildingBlocks.Domain;

namespace AICOS.Payments.Api.Domain;

/// <summary>Mock needs no credentials. Sandbox talks to a provider's test environment. Live is out of scope for the MVP.</summary>
public enum ProviderMode
{
    Mock,
    Sandbox,
}

/// <summary>
/// A payment provider the platform supports (UC-08, FR-06): the global configuration shared by every tenant.
/// The API key and secret key are stored encrypted and are never returned by the API.
/// </summary>
public sealed class PaymentProvider : AuditableEntity<Guid>
{
    /// <summary>The providers the platform can integrate with (task: VietQR, Momo, ZaloPay, plus the mock used for demos).</summary>
    public static readonly IReadOnlyList<string> SupportedCodes = ["Mock", "VietQR", "Momo", "ZaloPay"];

    public const string MockCode = "Mock";
    public const int DisplayNameMaxLength = 100;
    public const int MerchantIdMaxLength = 100;
    public const int CallbackUrlMaxLength = 500;

    private PaymentProvider()
    {
    }

    public PaymentProvider(string code, string displayName, ProviderMode mode)
    {
        Id = Guid.NewGuid();
        Code = code;
        DisplayName = displayName.Trim();
        Mode = mode;
    }

    /// <summary>One of <see cref="SupportedCodes"/>; unique.</summary>
    public string Code { get; private set; } = string.Empty;

    public string DisplayName { get; private set; } = string.Empty;

    public ProviderMode Mode { get; private set; }

    /// <summary>Whether tenants may use this provider. A real provider needs its credentials first.</summary>
    public bool IsEnabled { get; private set; }

    public string? MerchantId { get; private set; }

    public string? ApiKeyEncrypted { get; private set; }

    public string? SecretKeyEncrypted { get; private set; }

    /// <summary>Where the provider sends payment results (the webhook of the Payment service).</summary>
    public string? CallbackUrl { get; private set; }

    public bool IsMock => Code == MockCode;

    public bool HasCredentials => !string.IsNullOrEmpty(MerchantId) && ApiKeyEncrypted is not null && SecretKeyEncrypted is not null;

    public void Update(string displayName, ProviderMode mode, string? merchantId, string? callbackUrl)
    {
        DisplayName = displayName.Trim();
        Mode = mode;
        MerchantId = string.IsNullOrWhiteSpace(merchantId) ? null : merchantId.Trim();
        CallbackUrl = string.IsNullOrWhiteSpace(callbackUrl) ? null : callbackUrl.Trim();
    }

    /// <summary>Stores new encrypted credentials. A null argument keeps the value already stored.</summary>
    public void SetCredentials(string? apiKeyEncrypted, string? secretKeyEncrypted)
    {
        ApiKeyEncrypted = apiKeyEncrypted ?? ApiKeyEncrypted;
        SecretKeyEncrypted = secretKeyEncrypted ?? SecretKeyEncrypted;
    }

    public void SetEnabled(bool enabled) => IsEnabled = enabled;
}
