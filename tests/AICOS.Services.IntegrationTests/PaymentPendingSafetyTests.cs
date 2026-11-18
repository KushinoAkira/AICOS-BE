using System.Net.Http.Json;
using System.Text.Json;
using AICOS.Payments.Api.Domain;
using AICOS.Payments.Api.Infrastructure;
using AICOS.Payments.Api.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AICOS.Services.IntegrationTests;

/// <summary>Real HTTP pipeline; EF InMemory and controlled provider, not PostgreSQL/provider E2E.</summary>
public sealed class PaymentPendingSafetyTests : PaymentTestBase
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expiry_or_local_cancel_never_authorizes_another_payment(bool cancel)
    {
        var order = Guid.NewGuid();
        var payment = await PayOnlineAsync(order, 50_000);
        if (cancel) await Customer.Post($"{Tx}/{payment.Id()}/cancel", new { }).ShouldBe(200);
        else
        {
            F.Clock.Now = F.Clock.Now.AddDays(2);
            await Customer.GetAsync($"{Tx}/{payment.Id()}");
        }
        await Customer.Post(Tx, new { orderId = order, branchId = B1, amount = 50_000, providerCode = "Mock" }).ShouldBe(409, "payment.online_pending");
        await Cashier1.Post($"{Tx}/cash", new { orderId = order, amount = 50_000 }).ShouldBe(409, "payment.online_pending");
        var settlement = await Cashier1.GetFromJsonAsync<JsonElement>($"/api/v1/payments/orders/{order}/settlement");
        settlement.GetProperty("state").GetString().Should().Be("Pending");
        settlement.GetProperty("hasPendingPayments").GetBoolean().Should().BeTrue();
        (await Customer.GetFromJsonAsync<JsonElement>($"{Tx}?orderId={order}")).GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task A_provider_failure_can_resolve_an_expired_attempt_without_erasing_its_history()
    {
        var order = Guid.NewGuid();
        var payment = await PayOnlineAsync(order, 50_000);
        F.Clock.Now = F.Clock.Now.AddDays(2);
        await Customer.GetAsync($"{Tx}/{payment.Id()}");
        using var scope = F.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var saved = await db.Payments.FindAsync(payment.Id());
        await scope.ServiceProvider.GetRequiredService<PaymentProcessor>().ApplyFailureAsync(saved!, "Confirmed by provider", default);
        saved!.Status.Should().Be(PaymentStatus.Expired);
        saved.RequiresReconciliation.Should().BeFalse();
        await Customer.Post(Tx, new { orderId = order, branchId = B1, amount = 50_000, providerCode = "Mock" }).ShouldBe(201);
    }
}

public sealed class PaymentCheckoutTimeoutTests : PaymentTestBase
{
    private sealed class TimeoutGateway : IPaymentGateway
    {
        public string Code => "Mock";
        public int Calls { get; private set; }
        public Task<GatewayCheckout> CreatePaymentAsync(GatewayPaymentRequest request, CancellationToken ct)
        {
            Calls++;
            throw new HttpRequestException("Controlled lost response");
        }
        public Task<GatewayStatus> QueryAsync(Payment payment, CancellationToken ct) =>
            Task.FromResult(new GatewayStatus(ProviderState.Pending, null, null));
        public Task<GatewayRefundResult> RefundAsync(Payment payment, decimal amount, string reason, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    public PaymentCheckoutTimeoutTests() : base(services =>
    {
        services.RemoveAll<GatewayRegistry>();
        services.AddSingleton(new GatewayRegistry([new TimeoutGateway()]));
    }) { }

    [Fact]
    public async Task Lost_provider_response_leaves_one_durable_attempt_and_retry_does_not_call_provider_again()
    {
        var order = Guid.NewGuid();
        var request = new { orderId = order, branchId = B1, amount = 50_000, providerCode = "Mock" };
        await Customer.Post(Tx, request).ShouldBe(503);
        var replay = await Customer.Post(Tx, request);
        replay.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var body = await replay.Body();
        body.GetProperty("status").GetString().Should().Be("Pending");
        body.GetProperty("checkoutUrl").ValueKind.Should().Be(JsonValueKind.Null);
        ((TimeoutGateway)F.Services.GetRequiredService<GatewayRegistry>().For("Mock")).Calls.Should().Be(1);
        (await Customer.GetFromJsonAsync<JsonElement>($"{Tx}?orderId={order}")).GetArrayLength().Should().Be(1);
    }
}
