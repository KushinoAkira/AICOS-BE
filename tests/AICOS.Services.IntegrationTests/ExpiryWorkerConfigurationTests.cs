using AICOS.Ordering.Api;
using AICOS.Ordering.Api.Features.Orders;
using AICOS.Ordering.Api.Features.Qr;
using AICOS.Ordering.Api.Persistence;
using AICOS.Payments.Api;
using AICOS.Payments.Api.Infrastructure;
using AICOS.Payments.Api.Persistence;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AICOS.Services.IntegrationTests;

public sealed class ExpiryWorkerConfigurationTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void Ordering_expiry_workers_follow_configuration(string? configured, bool expected)
    {
        using var factory = new ServiceFactory<OrderingService, OrderingDbContext>(settings: Settings(configured));
        var workers = factory.Services.GetServices<IHostedService>().ToArray();
        workers.OfType<PickupOrderExpiryWorker>().Single().ExecuteTask!.IsCompleted.Should().Be(!expected);
        workers.OfType<TableSessionExpiryWorker>().Single().ExecuteTask!.IsCompleted.Should().Be(!expected);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void Payment_expiry_worker_follows_configuration(string? configured, bool expected)
    {
        using var factory = new ServiceFactory<PaymentsService, PaymentsDbContext>(settings: Settings(configured));
        factory.Services.GetServices<IHostedService>().OfType<PaymentExpiryJob>().Single().ExecuteTask!.IsCompleted.Should().Be(!expected);
    }

    private static Dictionary<string, string?> Settings(string? configured) => configured is null
        ? []
        : new() { ["BackgroundJobs:ExpiryEnabled"] = configured };
}