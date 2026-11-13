using AICOS.Payments.Api.Domain;
using AICOS.Payments.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AICOS.Payments.Api.Persistence;

public static class PaymentsSeeder
{
    /// <summary>
    /// Makes sure the mock provider exists and is enabled on a fresh database, so the demo works without any credentials
    /// (the deliverable ships "mock payment configuration"). When <c>Payments:PayOs:ClientId</c>, <c>ApiKey</c> and <c>ChecksumKey</c> are
    /// configured it also creates the PayOS provider, enabled, with those keys (encrypted). Runs together with migrations; never touches an
    /// existing row: change payOS's keys later through the provider admin (PUT /providers/{id}).
    /// </summary>
    public static async Task SeedPaymentsAsync(this WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
        {
            return;
        }

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        if (!await db.PaymentProviders.AnyAsync(p => p.Code == PaymentProvider.MockCode))
        {
            var mock = new PaymentProvider(PaymentProvider.MockCode, "Mock payment (demo)", ProviderMode.Mock);
            mock.SetEnabled(true);
            db.PaymentProviders.Add(mock);
        }

        var payos = scope.ServiceProvider.GetRequiredService<IOptions<PayOsOptions>>().Value;
        if (!string.IsNullOrWhiteSpace(payos.ClientId) && !string.IsNullOrWhiteSpace(payos.ApiKey) && !string.IsNullOrWhiteSpace(payos.ChecksumKey)
            && !await db.PaymentProviders.AnyAsync(p => p.Code == PaymentProvider.PayOsCode))
        {
            var protector = scope.ServiceProvider.GetRequiredService<SecretProtector>();
            var provider = new PaymentProvider(PaymentProvider.PayOsCode, "payOS", ProviderMode.Sandbox);
            provider.Update("payOS", ProviderMode.Sandbox, payos.ClientId, callbackUrl: null);
            provider.SetCredentials(protector.Protect(payos.ApiKey.Trim()), protector.Protect(payos.ChecksumKey.Trim()));
            provider.SetEnabled(true);
            db.PaymentProviders.Add(provider);
        }

        await db.SaveChangesAsync();
    }
}
