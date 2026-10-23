using AICOS.Payments.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace AICOS.Payments.Api.Persistence;

public static class PaymentsSeeder
{
    /// <summary>
    /// Makes sure the mock provider exists and is enabled on a fresh database, so the demo works without any credentials
    /// (the deliverable ships "mock payment configuration"). Runs together with migrations; never touches an existing row.
    /// </summary>
    public static async Task SeedPaymentsAsync(this WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
        {
            return;
        }

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        if (await db.PaymentProviders.AnyAsync(p => p.Code == PaymentProvider.MockCode))
        {
            return;
        }

        var mock = new PaymentProvider(PaymentProvider.MockCode, "Mock payment (demo)", ProviderMode.Mock);
        mock.SetEnabled(true);
        db.PaymentProviders.Add(mock);
        await db.SaveChangesAsync();
    }
}
