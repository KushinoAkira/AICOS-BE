using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Payments.Api.Domain;
using AICOS.Payments.Api.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AICOS.Services.IntegrationTests;

/// <summary>Real HTTP handlers, EF InMemory and fake Ordering; not PostgreSQL concurrency certification.</summary>
public sealed class PaymentSettlementTests : PaymentTestBase
{
    private static string Path(Guid order) => $"/api/v1/payments/orders/{order}/settlement";

    [Fact]
    public async Task Cash_needs_order_projection_sync_and_reading_does_not_publish_events()
    {
        var order = Guid.NewGuid();
        Orders.Set(order, B1, 50_000);
        var empty = await Cashier1.GetFromJsonAsync<JsonElement>(Path(order));
        empty.GetProperty("state").GetString().Should().Be("AwaitingPayment");
        await PaidCashAsync(50_000, order);
        var eventCount = F.Events.Published.Count;
        var waiting = await Cashier1.GetFromJsonAsync<JsonElement>(Path(order));
        waiting.GetProperty("state").GetString().Should().Be("AwaitingOrderSync");
        waiting.GetProperty("isSettled").GetBoolean().Should().BeFalse();
        Orders.Set(order, B1, 50_000, paymentStatus: "Paid");
        var paid = await Cashier1.GetFromJsonAsync<JsonElement>(Path(order));
        paid.GetProperty("isSettled").GetBoolean().Should().BeTrue();
        paid.GetProperty("advisoryOnly").GetBoolean().Should().BeTrue();
        paid.GetProperty("outstandingAmount").GetDecimal().Should().Be(0);
        F.Events.Published.Count.Should().Be(eventCount);
    }

    [Fact]
    public async Task Pending_attempt_is_not_expired_by_a_settlement_read()
    {
        var order = Guid.NewGuid();
        await PayOnlineAsync(order, 50_000);
        F.Clock.Now = F.Clock.Now.AddHours(2);
        var result = await Cashier1.GetFromJsonAsync<JsonElement>(Path(order));
        result.GetProperty("state").GetString().Should().Be("Pending");
        result.GetProperty("isSettled").GetBoolean().Should().BeFalse();
        var history = await Cashier1.GetFromJsonAsync<JsonElement>($"{Tx}?orderId={order}");
        history[0].GetProperty("status").GetString().Should().Be("Pending");
    }

    [Fact]
    public async Task Duplicate_cash_records_require_reconciliation_even_when_order_is_paid()
    {
        var order = Guid.NewGuid();
        await PaidCashAsync(50_000, order);
        Orders.Set(order, B1, 50_000, paymentStatus: "Paid");
        using (var scope = F.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            var duplicate = new Payment(Tenant, B1, order, 50_000, Payment.CashMethod, "Corrupt fixture for reconciliation test");
            duplicate.RecordCash(CashierId, F.Clock.Now.UtcDateTime);
            db.Payments.Add(duplicate);
            await db.SaveChangesAsync();
        }
        var result = await Cashier1.GetFromJsonAsync<JsonElement>(Path(order));
        result.GetProperty("state").GetString().Should().Be("ReconciliationRequired");
        result.GetProperty("isSettled").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task A_refund_waiting_for_approval_blocks_settlement()
    {
        var order = Guid.NewGuid();
        var payment = await PaidCashAsync(50_000, order);
        Orders.Set(order, B1, 50_000, paymentStatus: "Paid");
        await Manager1.Post($"{Tx}/{payment.Id()}/refunds", new {
            kind = "Exception", amount = 10_000, reason = "Wrong item, awaiting review"
        }).ShouldBe(201);
        var result = await Cashier1.GetFromJsonAsync<JsonElement>(Path(order));
        result.GetProperty("hasUnresolvedRefunds").GetBoolean().Should().BeTrue();
        result.GetProperty("state").GetString().Should().Be("ReconciliationRequired");
        result.GetProperty("isSettled").GetBoolean().Should().BeFalse();
    }

    [Theory]
    [InlineData("Cancelled")]
    [InlineData("NoShow")]
    [InlineData("Unfulfillable")]
    public async Task A_voided_order_has_no_obligation_until_money_has_been_received(string status)
    {
        var order = Guid.NewGuid();
        Orders.Set(order, B1, 50_000, status: status);
        var empty = await Cashier1.GetFromJsonAsync<JsonElement>(Path(order));
        empty.GetProperty("isSettled").GetBoolean().Should().BeTrue();
        Orders.Set(order, B1, 50_000);
        await PaidCashAsync(50_000, order);
        Orders.Set(order, B1, 50_000, status: status, paymentStatus: "Paid");
        var paid = await Cashier1.GetFromJsonAsync<JsonElement>(Path(order));
        paid.GetProperty("state").GetString().Should().Be("ReconciliationRequired");
        paid.GetProperty("isSettled").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Settlement_is_staff_only_and_branch_scoped()
    {
        var order = Guid.NewGuid();
        Orders.Set(order, B1, 50_000);
        (await F.CreateClient().GetAsync(Path(order))).StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);
        (await Customer.GetAsync(Path(order))).StatusCode.Should().Be(System.Net.HttpStatusCode.Forbidden);
        (await Cashier2.GetAsync(Path(order))).StatusCode.Should().Be(System.Net.HttpStatusCode.Forbidden);
        using var barista = F.As(AicosRoles.Barista, B1);
        (await barista.GetAsync(Path(order))).EnsureSuccessStatusCode();
    }
}
