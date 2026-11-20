using System.Net.Http.Json;
using AICOS.Payments.Api.Features.SessionBilling;
using AICOS.Payments.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AICOS.Services.IntegrationTests;

// Real service handlers with InMemory/Identity/JWT/transport doubles, not provider sandbox E2E.
public sealed class PaymentMainCompatibilityTests
{
    [Fact]
    public async Task A_new_order_provider_cannot_be_used_to_bypass_the_session_bill_contract()
    {
        using var h = new SessionBillHarness(); await h.InitializeAsync(); await h.RoundAsync(); await h.RoundAsync();
        var bill = await h.BillAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/payments/bills/{bill.Id}/payments/qr")
        { Content = JsonContent.Create(new CollectBillRequest(h.Session, bill.Fingerprint, null, false, "ZaloPay")) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        await h.Cashier.SendAsync(request).ShouldBe(503);
        using var scope = h.Payments.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().BillAttempts.CountAsync()).Should().Be(0);
        (await h.AddRoundAsync("after-unsupported-qr-provider")).EnsureSuccessStatusCode();
        (await h.BillAsync()).Quote.Total.Should().Be(150000);
    }
}
