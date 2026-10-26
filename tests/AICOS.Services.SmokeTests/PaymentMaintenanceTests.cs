using System.Net;
using AICOS.Payments.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace AICOS.Services.SmokeTests;

public sealed class PaymentMaintenanceTests
{
    [Theory]
    [InlineData("POST", "/api/v1/payments/bills")]
    [InlineData("POST", "/api/v1/payments/bills/00000000-0000-0000-0000-000000000001/payments/cash")]
    [InlineData("POST", "/api/v1/payments/bills/00000000-0000-0000-0000-000000000001/payments/qr")]
    [InlineData("POST", "/api/v1/payments/payments/00000000-0000-0000-0000-000000000001/confirm")]
    [InlineData("POST", "/api/v1/payments/bills/00000000-0000-0000-0000-000000000001/close")]
    [InlineData("PATCH", "/api/v1/payments/future")]
    [InlineData("DELETE", "/api/v1/payments/future")]
    public async Task Maintenance_rejects_mutations_before_database_access(string method, string path)
    {
        await using var factory = CreateFactory(false);
        using var client = factory.CreateClient();
        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(60), response.Headers.RetryAfter?.Delta);
    }

    [Fact]
    public async Task Maintenance_keeps_health_and_reads_available_without_bypassing_authentication()
    {
        await using var factory = CreateFactory(false);
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/payments/_info")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/payments/bills")).StatusCode);
    }

    [Fact]
    public async Task Enabling_mutations_restores_normal_authentication_pipeline()
    {
        await using var factory = CreateFactory(true);
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/v1/payments/bills", null)).StatusCode);
    }

    private static WebApplicationFactory<PaymentsService> CreateFactory(bool enabled) =>
        new WebApplicationFactory<PaymentsService>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Payments:MutationsEnabled"] = enabled.ToString() })));
}
