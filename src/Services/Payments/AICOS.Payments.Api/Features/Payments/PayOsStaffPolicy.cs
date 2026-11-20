using AICOS.Payments.Api.Domain;
using AICOS.Payments.Api.Infrastructure;
using AICOS.Payments.Api.Persistence;

namespace AICOS.Payments.Api.Features.Payments;

internal static class PayOsStaffPolicy
{
    public static bool MatchesChannel(PayOsOptions options, Guid tenant) => options.MerchantTenantId == tenant
        && options.ReceivingAccountNumber is { Length: > 0 and <= 40 } account && account.All(char.IsAsciiDigit)
        && Uri.TryCreate(options.ApiUrl, UriKind.Absolute, out var api) && api.Scheme == "https" && api.Host == "api-merchant.payos.vn"
        && api.Port == 443 && api.UserInfo == "" && api.AbsolutePath == "/" && api.Query == "" && api.Fragment == "";

    public static async Task<bool> AvailableAsync(PaymentsDbContext db, SecretProtector protector, PayOsOptions options,
        Guid tenant, Guid branch, CancellationToken ct)
    {
        if (!options.StaffPaymentsEnabled || !MatchesChannel(options, tenant) || !PublicHttps(options.ReturnUrl) || !PublicHttps(options.ResultPageUrl)
            || await PayOsCredentials.LoadAsync(db, protector, ct) is null) return false;
        var merchant = await MerchantResolver.ResolveAsync(db, tenant, branch, PaymentProvider.PayOsCode, ct);
        if (merchant is null || !PublicHttps(merchant.Provider.CallbackUrl ?? "")) return false;
        var callback = new Uri(merchant.Provider.CallbackUrl!);
        return callback.Host == new Uri(options.ReturnUrl).Host && callback.AbsolutePath == "/api/v1/payments/payos/webhook"
            && new Uri(options.ReturnUrl).AbsolutePath == "/api/v1/payments/payos/return";
    }

    private static bool PublicHttps(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && !uri.IsLoopback && uri.UserInfo == "" && uri.Fragment == "";
}
