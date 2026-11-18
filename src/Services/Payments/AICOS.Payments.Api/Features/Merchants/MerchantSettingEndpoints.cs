using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Security;
using AICOS.BuildingBlocks.Validation;
using AICOS.Payments.Api.Domain;
using AICOS.Payments.Api.Infrastructure;
using AICOS.Payments.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.Payments.Api.Features.Merchants;

/// <summary>
/// PUT /merchant-settings: switch a provider on or off for the brand (empty <see cref="BranchId"/>) or for one branch, with the receiving
/// account. The account number is optional on updates: a missing value keeps the stored one.
/// </summary>
public sealed record MerchantSettingRequest(
    Guid? BranchId, string? ProviderCode, bool? IsEnabled, string? AccountName, string? BankCode, string? AccountNumber, string? MerchantCode);

/// <summary>The account number is never returned in full: only whether it is set and its last 4 digits.</summary>
public sealed record MerchantSettingResponse(
    Guid Id, Guid? BranchId, string ProviderCode, bool IsEnabled, string? AccountName, string? BankCode, bool HasAccountNumber, string? AccountNumberHint,
    string? MerchantCode, DateTime CreatedAtUtc, DateTime? UpdatedAtUtc)
{
    public static MerchantSettingResponse From(MerchantSetting s) => new(
        s.Id, s.BranchId, s.ProviderCode, s.IsEnabled, s.AccountName, s.BankCode, s.AccountNumberEncrypted is not null,
        s.AccountNumberHint is null ? null : $"••••{s.AccountNumberHint}", s.MerchantCode, s.CreatedAtUtc, s.UpdatedAtUtc);
}

/// <summary>A payment method a customer can use at a branch.</summary>
public sealed record AvailablePaymentMethod(string ProviderCode, string DisplayName, ProviderMode Mode, string Source);

/// <summary>
/// UC-20 Configure Merchant Payment Settings. Routes: /api/v1/payments/merchant-settings. BrandAdmin writes; the BranchManager reads the
/// settings that apply to their branch; <c>GET /available</c> tells any signed-in user of the brand which methods a branch accepts.
/// A provider can only be enabled when the platform has enabled it globally (UC-08). Account changes are sent to the audit history.
/// </summary>
public static class MerchantSettingEndpoints
{
    public static void MapMerchantSettingEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/merchant-settings").WithTags("Merchant settings");

        group.MapGet("/", ListAsync).RequireAuthorization(AicosPolicies.Management);
        group.MapGet("/available", AvailableAsync).RequireAuthorization();
        group.MapPut("/", UpsertAsync).RequireAuthorization(AicosPolicies.AdminOnly);
        group.MapDelete("/{id:guid}", DeleteAsync).RequireAuthorization(AicosPolicies.AdminOnly);
    }

    /// <summary>The brand admin sees every setting of the brand; a branch manager sees the brand-wide ones and those of their own branch.</summary>
    private static async Task<List<MerchantSettingResponse>> ListAsync(PaymentsDbContext db, ICurrentUser user, Guid? branchId, CancellationToken ct)
    {
        var tenantId = TenantScope.Require(user);
        var query = db.MerchantSettings.AsNoTracking().Where(s => s.TenantId == tenantId);
        if (user.IsInRole(AicosRoles.BranchManager))
        {
            var own = user.BranchId ?? throw new ForbiddenException("Your account is not assigned to a branch.");
            query = query.Where(s => s.BranchId == null || s.BranchId == own);
        }
        else if (branchId is not null)
        {
            query = query.Where(s => s.BranchId == branchId);
        }

        var settings = await query.OrderBy(s => s.ProviderCode).ThenBy(s => s.BranchId).ToListAsync(ct);
        return settings.Select(MerchantSettingResponse.From).ToList();
    }

    /// <summary>
    /// The methods accepted at a branch: for every provider the platform has enabled, the branch setting if there is one, otherwise the
    /// brand-wide one. A provider nobody set up for this brand is not offered, except the mock, which needs no account.
    /// </summary>
    private static async Task<List<AvailablePaymentMethod>> AvailableAsync(PaymentsDbContext db, ICurrentUser user, Guid? branchId, CancellationToken ct)
    {
        var tenantId = TenantScope.Require(user);
        var branch = branchId ?? user.BranchId;
        var providers = await db.PaymentProviders.AsNoTracking().Where(p => p.IsEnabled).ToListAsync(ct);
        var settings = await db.MerchantSettings.AsNoTracking()
            .Where(s => s.TenantId == tenantId && (s.BranchId == null || s.BranchId == branch))
            .ToListAsync(ct);

        var methods = new List<AvailablePaymentMethod>();
        foreach (var provider in providers.OrderBy(p => p.Code))
        {
            var forBranch = settings.FirstOrDefault(s => s.ProviderCode == provider.Code && s.BranchId == branch && branch != null);
            var forBrand = settings.FirstOrDefault(s => s.ProviderCode == provider.Code && s.BranchId == null);
            var effective = forBranch ?? forBrand;

            if (effective is null)
            {
                if (provider.IsMock)
                {
                    methods.Add(new AvailablePaymentMethod(provider.Code, provider.DisplayName, provider.Mode, "Platform"));
                }

                continue;
            }

            if (effective.IsEnabled)
            {
                methods.Add(new AvailablePaymentMethod(provider.Code, provider.DisplayName, provider.Mode, forBranch is not null ? "Branch" : "Brand"));
            }
        }

        return methods;
    }

    private static async Task<IResult> UpsertAsync(
        MerchantSettingRequest r,
        PaymentsDbContext db,
        SecretProtector protector,
        ICurrentUser user,
        IBranchDirectory directory,
        IEventBus bus,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var code = Validate(r);
        var tenantId = TenantScope.Require(user);
        if (r.BranchId is not null)
        {
            await BranchScope.EnsureUsableAsync(directory, r.BranchId.Value, requireActive: false, ct);
        }

        var provider = await db.PaymentProviders.AsNoTracking().FirstOrDefaultAsync(p => p.Code == code, ct)
            ?? throw new BusinessRuleException("merchant.provider_unavailable", $"{code} is not configured on the platform.");
        if (!provider.IsEnabled && r.IsEnabled == true)
        {
            throw new BusinessRuleException("merchant.provider_disabled", $"{code} is not enabled on the platform yet.");
        }

        var setting = await db.MerchantSettings.FirstOrDefaultAsync(s => s.TenantId == tenantId && s.BranchId == r.BranchId && s.ProviderCode == code, ct);
        var created = setting is null;
        var before = created ? null : Snapshot(setting!);
        setting ??= db.MerchantSettings.Add(new MerchantSetting(tenantId, r.BranchId, code)).Entity;

        setting.Update(r.IsEnabled!.Value, r.AccountName, r.BankCode, r.MerchantCode);
        if (!string.IsNullOrWhiteSpace(r.AccountNumber))
        {
            var number = r.AccountNumber.Trim();
            setting.SetAccountNumber(protector.Protect(number), number.Length >= 4 ? number[^4..] : number);
        }

        if (provider.NeedsBrandAccount && setting.IsEnabled && !setting.HasPaymentDetails)
        {
            throw new BusinessRuleException("merchant.details_missing", $"Give the receiving account or the merchant code before enabling {code}.");
        }

        await db.SaveChangesAsync(ct);
        await AuditTrail.PublishAsync(
            bus, user, loggers.CreateLogger("AICOS.Payments.Merchants"), tenantId, created ? "merchant_setting.created" : "merchant_setting.updated", "MerchantSetting",
            setting.Id, before, Snapshot(setting));

        return created
            ? Results.Created($"/api/v1/payments/merchant-settings/{setting.Id}", MerchantSettingResponse.From(setting))
            : Results.Ok(MerchantSettingResponse.From(setting));
    }

    private static async Task<IResult> DeleteAsync(
        Guid id, PaymentsDbContext db, ICurrentUser user, IEventBus bus, ILoggerFactory loggers, CancellationToken ct)
    {
        var tenantId = TenantScope.Require(user);
        var setting = await db.MerchantSettings.FirstOrDefaultAsync(s => s.Id == id && s.TenantId == tenantId, ct) ?? throw new NotFoundException("Merchant setting", id);
        var before = Snapshot(setting);

        db.MerchantSettings.Remove(setting);
        await db.SaveChangesAsync(ct);

        await AuditTrail.PublishAsync(bus, user, loggers.CreateLogger("AICOS.Payments.Merchants"), tenantId, "merchant_setting.deleted", "MerchantSetting", id, before);
        return Results.NoContent();
    }

    private static string Validate(MerchantSettingRequest r)
    {
        var errors = new ValidationErrors();
        var code = PaymentProvider.SupportedCodes.FirstOrDefault(c => string.Equals(c, r.ProviderCode?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (code is null)
        {
            errors.Add("ProviderCode", $"Provider must be one of: {string.Join(", ", PaymentProvider.SupportedCodes)}.");
        }

        if (r.IsEnabled is null)
        {
            errors.Add("IsEnabled", "IsEnabled is required (true or false).");
        }

        CommonRules.MaxLength(errors, "AccountName", r.AccountName, "Account name", MerchantSetting.AccountNameMaxLength);
        CommonRules.MaxLength(errors, "BankCode", r.BankCode, "Bank code", MerchantSetting.BankCodeMaxLength);
        CommonRules.MaxLength(errors, "MerchantCode", r.MerchantCode, "Merchant code", MerchantSetting.MerchantCodeMaxLength);
        if (!string.IsNullOrWhiteSpace(r.AccountNumber) && (r.AccountNumber.Trim().Length is < 6 or > 30 || !r.AccountNumber.Trim().All(char.IsLetterOrDigit)))
        {
            errors.Add("AccountNumber", "Account number must be 6-30 letters or digits.");
        }

        if (code == PaymentProvider.MockCode && (r.AccountName is not null || r.BankCode is not null || r.AccountNumber is not null || r.MerchantCode is not null))
        {
            errors.Add("ProviderCode", "The Mock provider takes no account details.");
        }

        errors.ThrowIfAny();
        return code!;
    }

    /// <summary>Audit snapshot: the account number appears only as its last digits.</summary>
    private static object Snapshot(MerchantSetting s) => new
    {
        s.ProviderCode, s.BranchId, s.IsEnabled, s.AccountName, s.BankCode, s.MerchantCode, HasAccountNumber = s.AccountNumberEncrypted is not null, s.AccountNumberHint,
    };
}
