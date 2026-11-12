using AICOS.BuildingBlocks.Security;

namespace AICOS.Ordering.Api.Features.KitchenAdmission;

public static class KitchenAdmissionRegistration
{
    public static void AddKitchenAdmission(this WebApplicationBuilder builder)
    {
        builder.Services.AddHttpClient<IKitchenAdmissionTransport, HttpKitchenAdmissionTransport>(client =>
        {
            client.BaseAddress = new Uri(builder.Configuration["Services:Kitchen:BaseUrl"] ?? "http://localhost:5107");
            client.Timeout = TimeSpan.FromSeconds(8);
        });
        builder.Services.AddScoped<KitchenAdmissionDispatcher>();
        builder.Services.AddScoped<KitchenAdmissionReconciler>();
        if (builder.Configuration.GetValue<bool>("KitchenAdmission:DispatcherEnabled") || builder.Configuration.GetValue<bool>("KitchenAdmission:ReconciliationEnabled")
            || builder.Configuration.GetValue<bool>("CupServing:AdmissionDispatchEnabled") || builder.Configuration.GetValue<bool>("CupServing:AdmissionReconciliationEnabled"))
            AdmissionMachineAuthentication.RequireKey(builder.Configuration);
        builder.Services.AddHostedService<KitchenAdmissionWorker>();
    }
}

public sealed class KitchenAdmissionWorker(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<KitchenAdmissionWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(
        LoopAsync("DispatcherEnabled", TimeSpan.FromSeconds(2), async (services, ct) =>
            await services.GetRequiredService<KitchenAdmissionDispatcher>().DispatchOnceAsync(ct), stoppingToken),
        LoopAsync("ReconciliationEnabled", TimeSpan.FromSeconds(15), async (services, ct) =>
            await services.GetRequiredService<KitchenAdmissionReconciler>().ReconcileOnceAsync(ct), stoppingToken),
        LoopAsync("CupServing:AdmissionDispatchEnabled", TimeSpan.FromSeconds(2), async (services, ct) =>
            await services.GetRequiredService<KitchenAdmissionDispatcher>().DispatchOnceAsync(ct), stoppingToken),
        LoopAsync("CupServing:AdmissionReconciliationEnabled", TimeSpan.FromSeconds(15), async (services, ct) =>
            await services.GetRequiredService<KitchenAdmissionReconciler>().ReconcileOnceAsync(ct, cups: true), stoppingToken));

    private async Task LoopAsync(string flag, TimeSpan interval, Func<IServiceProvider, CancellationToken, Task<int>> operation, CancellationToken ct)
    {
        if (!configuration.GetValue<bool>(flag.Contains(':') ? flag : "KitchenAdmission:" + flag)) return;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await operation(scope.ServiceProvider, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception failure)
            {
                // Never dump a request/response/connection string or signing key.
                logger.LogWarning("Kitchen admission {Worker} failed ({Type}); durable state retained for retry.", flag, failure.GetType().Name);
            }
            try { await Task.Delay(interval, ct); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }
}
