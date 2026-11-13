using System.Text.Json;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Persistence;
using AICOS.BuildingBlocks.Security;
using AICOS.BuildingBlocks.Validation;
using AICOS.Contracts.Events;
using AICOS.Payments.Api.Domain;
using AICOS.Payments.Api.Infrastructure;
using AICOS.Payments.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.Payments.Api.Features.Providers;

/// <summary>POST /providers. <see cref="Code"/> is one of Mock, VietQR, Momo, ZaloPay, PayOS and cannot be changed later.</summary>
public sealed record CreateProviderRequest(
    string? Code, string? DisplayName, ProviderMode? Mode, string? MerchantId, string? ApiKey, string? SecretKey, string? CallbackUrl);

/// <summary>PUT /providers/{id}. A missing <c>apiKey</c> / <c>secretKey</c> keeps the stored one; a value replaces it.</summary>
public sealed record UpdateProviderRequest(
    string? DisplayName, ProviderMode? Mode, string? MerchantId, string? ApiKey, string? SecretKey, string? CallbackUrl);

public sealed record ProviderStatusRequest(bool? IsEnabled);

/// <summary>Credentials are never returned: only whether they are set, and the last 4 characters of the API key to tell keys apart.</summary>
public sealed record ProviderResponse(
    Guid Id,
    string Code,
    string DisplayName,
    ProviderMode Mode,
    bool IsEnabled,
    string? MerchantId,
    bool HasApiKey,
    string? ApiKeyHint,
    bool HasSecretKey,
    string? CallbackUrl,
    DateTime CreatedAtUtc,
    DateTime? UpdatedAtUtc);

/// <summary>What a tenant may see when it chooses how to get paid: no merchant id, no keys.</summary>
public sealed record EnabledProviderResponse(string Code, string DisplayName, ProviderMode Mode);

/// <summary>
/// UC-08 Configure Payment Providers (mock/sandbox). Routes: /api/v1/payments/providers. SuperAdmin only, except
/// <c>GET /providers/enabled</c> which any signed-in user may call. Credentials are encrypted at rest and never returned.
/// </summary>
public static class ProviderEndpoints
{
    private const int MaxKeyLength = 500;

    private static readonly JsonSerializerOptions AuditJson = new(JsonSerializerDefaults.Web);

    public static void MapProviderEndpoints(this RouteGroupBuilder api)
    {
        var providers = api.MapGroup("/providers").WithTags("Payment providers");

        providers.MapGet("/enabled", ListEnabledAsync).RequireAuthorization();

        providers.MapGet("/", ListAsync).RequireAuthorization(AicosPolicies.PlatformAdmin);
        providers.MapGet("/{id:guid}", GetAsync).WithName("GetProvider").RequireAuthorization(AicosPolicies.PlatformAdmin);
        providers.MapPost("/", CreateAsync).RequireAuthorization(AicosPolicies.PlatformAdmin);
        providers.MapPut("/{id:guid}", UpdateAsync).RequireAuthorization(AicosPolicies.PlatformAdmin);
        providers.MapPatch("/{id:guid}/status", ChangeStatusAsync).RequireAuthorization(AicosPolicies.PlatformAdmin);
    }

    private static async Task<List<ProviderResponse>> ListAsync(PaymentsDbContext db, SecretProtector protector, bool? isEnabled, CancellationToken ct)
    {
        var query = db.PaymentProviders.AsNoTracking();
        if (isEnabled is not null)
        {
            query = query.Where(p => p.IsEnabled == isEnabled);
        }

        var providers = await query.OrderBy(p => p.Code).ToListAsync(ct);
        return providers.Select(p => ToResponse(p, protector)).ToList();
    }

    private static async Task<ProviderResponse> GetAsync(Guid id, PaymentsDbContext db, SecretProtector protector, CancellationToken ct) =>
        ToResponse(await FindAsync(db, id, ct), protector);

    private static async Task<List<EnabledProviderResponse>> ListEnabledAsync(PaymentsDbContext db, CancellationToken ct) =>
        await db.PaymentProviders.AsNoTracking()
            .Where(p => p.IsEnabled)
            .OrderBy(p => p.Code)
            .Select(p => new EnabledProviderResponse(p.Code, p.DisplayName, p.Mode))
            .ToListAsync(ct);

    private static async Task<IResult> CreateAsync(
        CreateProviderRequest request, PaymentsDbContext db, SecretProtector protector, IEventBus bus, ICurrentUser user, ILoggerFactory loggers, CancellationToken ct)
    {
        var code = ValidateCreate(request);
        if (await db.PaymentProviders.AnyAsync(p => p.Code == code, ct))
        {
            throw new ConflictException($"Provider '{code}' is already configured.", "provider.duplicate_code");
        }

        var provider = new PaymentProvider(code, request.DisplayName!, request.Mode!.Value);
        provider.Update(request.DisplayName!, request.Mode.Value, request.MerchantId, request.CallbackUrl);
        provider.SetCredentials(Protect(protector, request.ApiKey), Protect(protector, request.SecretKey));

        db.PaymentProviders.Add(provider);
        await db.SaveChangesAsync(ct);
        await PublishAuditAsync(bus, user, loggers, "provider.created", provider, before: null, after: Snapshot(provider));

        return Results.CreatedAtRoute("GetProvider", new { id = provider.Id }, ToResponse(provider, protector));
    }

    private static async Task<ProviderResponse> UpdateAsync(
        Guid id, UpdateProviderRequest request, PaymentsDbContext db, SecretProtector protector, IEventBus bus, ICurrentUser user,
        ILoggerFactory loggers, CancellationToken ct)
    {
        var provider = await FindAsync(db, id, ct);
        ValidateUpdate(provider, request);

        var before = Snapshot(provider);
        provider.Update(request.DisplayName!, request.Mode!.Value, request.MerchantId, request.CallbackUrl);
        provider.SetCredentials(Protect(protector, request.ApiKey), Protect(protector, request.SecretKey));

        if (provider.IsEnabled && !provider.IsMock && !provider.HasCredentials)
        {
            throw new BusinessRuleException("provider.credentials_missing", "An enabled provider must keep its merchant id, API key and secret key.");
        }

        await db.SaveChangesAsync(ct);
        await PublishAuditAsync(bus, user, loggers, "provider.updated", provider, before, Snapshot(provider));

        return ToResponse(provider, protector);
    }

    /// <summary>Enabling a real provider needs its merchant id, API key and secret key first; the mock never does.</summary>
    private static async Task<ProviderResponse> ChangeStatusAsync(
        Guid id, ProviderStatusRequest request, PaymentsDbContext db, SecretProtector protector, IEventBus bus, ICurrentUser user,
        ILoggerFactory loggers, CancellationToken ct)
    {
        if (request.IsEnabled is null)
        {
            throw new ValidationFailedException(nameof(request.IsEnabled), "isEnabled is required (true or false).");
        }

        var provider = await FindAsync(db, id, ct);
        if (request.IsEnabled == true && !provider.IsMock && !provider.HasCredentials)
        {
            throw new BusinessRuleException(
                "provider.credentials_missing", $"Set the merchant id, API key and secret key of {provider.Code} before enabling it.");
        }

        var before = Snapshot(provider);
        provider.SetEnabled(request.IsEnabled.Value);
        await db.SaveChangesAsync(ct);
        await PublishAuditAsync(bus, user, loggers, "provider.status_changed", provider, before, Snapshot(provider));

        return ToResponse(provider, protector);
    }

    // ---------------------------------------------------------------- validation
    private static string ValidateCreate(CreateProviderRequest r)
    {
        var errors = new ValidationErrors();
        var code = PaymentProvider.SupportedCodes.FirstOrDefault(c => string.Equals(c, r.Code?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (code is null)
        {
            errors.Add(nameof(r.Code), $"Code must be one of: {string.Join(", ", PaymentProvider.SupportedCodes)}.");
        }

        ValidateCommon(errors, code, r.DisplayName, r.Mode, r.MerchantId, r.ApiKey, r.SecretKey, r.CallbackUrl);
        errors.ThrowIfAny();
        return code!;
    }

    private static void ValidateUpdate(PaymentProvider provider, UpdateProviderRequest r)
    {
        var errors = new ValidationErrors();
        ValidateCommon(errors, provider.Code, r.DisplayName, r.Mode, r.MerchantId, r.ApiKey, r.SecretKey, r.CallbackUrl);
        errors.ThrowIfAny();
    }

    private static void ValidateCommon(
        ValidationErrors errors, string? code, string? displayName, ProviderMode? mode, string? merchantId, string? apiKey, string? secretKey, string? callbackUrl)
    {
        CommonRules.Required(errors, "DisplayName", displayName, "Display name", PaymentProvider.DisplayNameMaxLength);
        CommonRules.MaxLength(errors, "MerchantId", merchantId, "Merchant id", PaymentProvider.MerchantIdMaxLength);
        CommonRules.MaxLength(errors, "ApiKey", apiKey, "API key", MaxKeyLength);
        CommonRules.MaxLength(errors, "SecretKey", secretKey, "Secret key", MaxKeyLength);

        if (mode is null || !Enum.IsDefined(mode.Value))
        {
            errors.Add("Mode", "Mode must be Mock or Sandbox.");
        }
        else if (code == PaymentProvider.MockCode && mode != ProviderMode.Mock)
        {
            errors.Add("Mode", "The Mock provider can only run in Mock mode.");
        }
        else if (code is not null && code != PaymentProvider.MockCode && mode != ProviderMode.Sandbox)
        {
            errors.Add("Mode", $"{code} runs in Sandbox mode (live payments are not part of the MVP).");
        }

        if (code == PaymentProvider.MockCode && (merchantId is not null || apiKey is not null || secretKey is not null))
        {
            errors.Add("ApiKey", "The Mock provider takes no merchant id or keys.");
        }

        if (!string.IsNullOrWhiteSpace(callbackUrl))
        {
            if (!Uri.TryCreate(callbackUrl.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                errors.Add("CallbackUrl", "Callback URL must be an absolute http(s) address.");
            }
            else
            {
                CommonRules.MaxLength(errors, "CallbackUrl", callbackUrl, "Callback URL", PaymentProvider.CallbackUrlMaxLength);
            }
        }
    }

    // ---------------------------------------------------------------- helpers
    private static string? Protect(SecretProtector protector, string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : protector.Protect(value.Trim());

    private static ProviderResponse ToResponse(PaymentProvider p, SecretProtector protector) => new(
        p.Id, p.Code, p.DisplayName, p.Mode, p.IsEnabled, p.MerchantId, p.ApiKeyEncrypted is not null, ApiKeyHint(p, protector),
        p.SecretKeyEncrypted is not null, p.CallbackUrl, p.CreatedAtUtc, p.UpdatedAtUtc);

    private static string? ApiKeyHint(PaymentProvider p, SecretProtector protector)
    {
        if (p.ApiKeyEncrypted is null)
        {
            return null;
        }

        try
        {
            var key = protector.Unprotect(p.ApiKeyEncrypted);
            return key.Length >= 8 ? $"••••{key[^4..]}" : "••••";
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
        {
            return "unreadable (secret key changed)";
        }
    }

    /// <summary>Audit snapshot: flags instead of secrets, so the history proves a key was set without exposing it.</summary>
    private static object Snapshot(PaymentProvider p) => new
    {
        p.Code, p.DisplayName, Mode = p.Mode.ToString(), p.IsEnabled, p.MerchantId, HasApiKey = p.ApiKeyEncrypted is not null,
        HasSecretKey = p.SecretKeyEncrypted is not null, p.CallbackUrl,
    };

    /// <summary>Platform-level audit entry (FR-67). Identity stores it; a failure to publish never fails the change itself.</summary>
    private static async Task PublishAuditAsync(
        IEventBus bus, ICurrentUser user, ILoggerFactory loggers, string action, PaymentProvider provider, object? before, object? after)
    {
        try
        {
            await bus.PublishAsync(new AuditRecordedEvent(
                TenantId: null,
                user.UserId,
                user.Name,
                user.Role,
                action,
                "PaymentProvider",
                provider.Id.ToString(),
                before is null ? null : JsonSerializer.Serialize(before, AuditJson),
                after is null ? null : JsonSerializer.Serialize(after, AuditJson)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            loggers.CreateLogger("AICOS.Payments.Providers").LogWarning(ex, "Could not publish the audit event {Action}", action);
        }
    }

    private static async Task<PaymentProvider> FindAsync(PaymentsDbContext db, Guid id, CancellationToken ct) =>
        await db.PaymentProviders.FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("Payment provider", id);
}
