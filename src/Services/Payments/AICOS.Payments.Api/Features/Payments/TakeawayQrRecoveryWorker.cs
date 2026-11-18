using AICOS.Payments.Api.Domain;
using AICOS.Payments.Api.Infrastructure;
using AICOS.Payments.Api.Persistence;
using Microsoft.EntityFrameworkCore;
namespace AICOS.Payments.Api.Features.Payments;

/// <summary>Replays durable payment truth after lossy Redis delivery; consumers deduplicate payment id.</summary>
public sealed class TakeawayQrRecoveryWorker(IServiceScopeFactory scopes, IConfiguration config,
    IHostEnvironment env, ILogger<TakeawayQrRecoveryWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!env.IsDevelopment() || !config.GetValue<bool>("TakeawayQr:Enabled")
            || !config.GetValue<bool>("TakeawayQr:AllowTestProvider")) return;
        var offset = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
                var processor = scope.ServiceProvider.GetRequiredService<PaymentProcessor>();
                var missing = await db.Payments.AsNoTracking().Where(p => p.Description.StartsWith(TakeawayQrEndpoints.Prefix)
                    && p.Method == "Mock" && p.Status == PaymentStatus.Pending && p.CheckoutUrl == null
                    && p.ExpiresAtUtc > processor.UtcNow).OrderBy(p => p.CreatedAtUtc).Take(100).ToListAsync(ct);
                foreach (var row in missing)
                {
                    var checkout = await scope.ServiceProvider.GetRequiredService<GatewayRegistry>().For("Mock")
                        .CreatePaymentAsync(new GatewayPaymentRequest(row.TransactionRef, row.Amount, "Takeaway test QR", row.ExpiresAtUtc, null, null, null, null), ct);
                    await PaymentOrderLock.RunAsync(db, row.TenantId, row.OrderId, async () =>
                    {
                        var current = await db.Payments.SingleAsync(p => p.Id == row.Id, ct);
                        if (current.Status == PaymentStatus.Pending && current.CheckoutUrl == null && current.ExpiresAtUtc > processor.UtcNow)
                        {
                            current.SetCheckout(checkout.CheckoutUrl, current.ExpiresAtUtc);
                            await db.SaveChangesAsync(ct);
                        }
                        return true;
                    }, ct);
                }
                var rows = await db.Payments.AsNoTracking().Where(p => p.Method == "Mock" && p.Description.StartsWith(TakeawayQrEndpoints.Prefix)
                    && p.Status == PaymentStatus.Succeeded && p.RefundedAmount == 0)
                    .OrderBy(p => p.CreatedAtUtc).ThenBy(p => p.Id).Skip(offset).Take(100).ToListAsync(ct);
                foreach (var row in rows) await scope.ServiceProvider.GetRequiredService<PaymentProcessor>().PublishSucceededAsync(row, ct);
                offset = rows.Count < 100 ? 0 : offset + rows.Count;
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                log.LogWarning("Takeaway QR replay will retry ({ErrorType}).", e.GetType().Name);
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }
}