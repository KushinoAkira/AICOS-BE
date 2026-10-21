using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;
using AICOS.Payments.Api.Features.Payments;

namespace AICOS.Services.IntegrationTests;

/// <summary>SCRUM-88 and 89 against the Order service: a payment is only taken for a real order, and reconciliation fetches the orders itself.</summary>
public sealed class PaymentOrderTests : PaymentTestBase
{
    [Fact]
    public async Task A_payment_for_an_order_that_does_not_exist_is_refused()
    {
        F.Observer = null;                                                                            // nothing registers the order on the fly

        await Customer.Post(Tx, new { orderId = Guid.NewGuid(), branchId = B1, amount = 50_000, providerCode = "Mock" }).ShouldBe(422, "payment.order_not_found");
        await Cashier1.Post($"{Tx}/cash", new { orderId = Guid.NewGuid(), amount = 50_000 }).ShouldBe(422, "payment.order_not_found");
    }

    [Fact]
    public async Task The_amount_must_be_what_the_order_costs()
    {
        var order = Guid.NewGuid();
        Orders.Set(order, B1, 80_000);

        await Customer.Post(Tx, new { orderId = order, branchId = B1, amount = 50_000, providerCode = "Mock" }).ShouldBe(422, "payment.amount_mismatch");
        await Cashier1.Post($"{Tx}/cash", new { orderId = order, amount = 79_999 }).ShouldBe(422, "payment.amount_mismatch");
        await Customer.Post(Tx, new { orderId = order, branchId = B1, amount = 80_000, providerCode = "Mock" }).ShouldBe(201);
    }

    [Fact]
    public async Task A_cancelled_or_already_paid_order_cannot_be_paid()
    {
        var cancelled = Guid.NewGuid();
        var paid = Guid.NewGuid();
        Orders.Set(cancelled, B1, 50_000, status: "Cancelled");
        Orders.Set(paid, B1, 50_000, status: "Confirmed", paymentStatus: "Paid");

        await Customer.Post(Tx, new { orderId = cancelled, branchId = B1, amount = 50_000, providerCode = "Mock" }).ShouldBe(422, "payment.order_cancelled");
        await Cashier1.Post($"{Tx}/cash", new { orderId = cancelled, amount = 50_000 }).ShouldBe(422, "payment.order_cancelled");
        await Customer.Post(Tx, new { orderId = paid, branchId = B1, amount = 50_000, providerCode = "Mock" }).ShouldBe(409, "payment.already_paid");
        await Cashier1.Post($"{Tx}/cash", new { orderId = paid, amount = 50_000 }).ShouldBe(409, "payment.already_paid");
    }

    [Fact]
    public async Task The_order_must_belong_to_the_branch_where_it_is_paid()
    {
        var order = Guid.NewGuid();
        Orders.Set(order, B2, 50_000);

        await Customer.Post(Tx, new { orderId = order, branchId = B1, amount = 50_000, providerCode = "Mock" }).ShouldBe(422, "payment.order_branch_mismatch");
        await Cashier1.Post($"{Tx}/cash", new { orderId = order, amount = 50_000 }).ShouldBe(422, "payment.order_branch_mismatch");
        await Cashier2.Post($"{Tx}/cash", new { orderId = order, amount = 50_000 }).ShouldBe(201);
    }

    [Fact]
    public async Task Cancelling_the_order_releases_a_payment_that_is_still_waiting()
    {
        var order = Guid.NewGuid();
        var waiting = await PayOnlineAsync(order, 50_000);
        var other = await PayOnlineAsync(amount: 20_000);

        await F.HandleAsync(new OrderCancelledIntegrationEvent(Tenant, order, B1, null, "x", false, [], DateTime.UtcNow));

        (await GetAsync(Customer, $"{Tx}/{waiting.Id()}")).GetProperty("status").GetString().Should().Be("Cancelled");
        (await GetAsync(Customer, $"{Tx}/{other.Id()}")).GetProperty("status").GetString().Should().Be("Pending");   // another order is untouched
        await F.HandleAsync(new OrderCancelledIntegrationEvent(Guid.NewGuid(), order, B1, null, "x", false, [], DateTime.UtcNow));   // another brand
    }

    // ------------------------------------------------------------------ automatic reconciliation (SCRUM-89)
    [Fact]
    public async Task Automatic_reconciliation_takes_the_orders_from_the_order_service_and_names_every_disagreement()
    {
        var ok = Guid.NewGuid();
        var paidNoPayment = Guid.NewGuid();
        var unpaidButPaid = Guid.NewGuid();
        var wrongAmount = Guid.NewGuid();
        var extra = Guid.NewGuid();
        await PaidOnlineAsync(100_000, ok);
        await PaidOnlineAsync(50_000, unpaidButPaid);
        await PaidCashAsync(70_000, wrongAmount);
        await PaidOnlineAsync(25_000, extra);
        Orders.Listed.AddRange(
        [
            new OrderForReconciliation(ok, 100_000, true),
            new OrderForReconciliation(paidNoPayment, 10_000, true),
            new OrderForReconciliation(unpaidButPaid, 50_000, false),
            new OrderForReconciliation(wrongAmount, 75_000, true),
        ]);

        var report = await Manager1.GetFromJsonAsync<JsonElement>("/api/v1/payments/reconciliation/auto");

        report.GetProperty("orders").GetInt32().Should().Be(4);
        report.GetProperty("matched").GetInt32().Should().Be(1);
        report.GetProperty("discrepancies").EnumerateArray().ToDictionary(d => d.GetProperty("orderId").GetGuid(), d => d.GetProperty("code").GetString()).Should().BeEquivalentTo(
            new Dictionary<Guid, string?>
            {
                [paidNoPayment] = "order_paid_without_payment",
                [unpaidButPaid] = "payment_without_paid_order",
                [wrongAmount] = "amount_mismatch",
                [extra] = "payment_without_order",
            });
    }

    [Fact]
    public async Task With_no_orders_every_paid_payment_is_reported_as_missing_its_order()
    {
        var paid = await PaidOnlineAsync(30_000);

        var report = await Manager1.GetFromJsonAsync<JsonElement>("/api/v1/payments/reconciliation/auto");

        report.GetProperty("orders").GetInt32().Should().Be(0);
        report.GetProperty("discrepancies").EnumerateArray().Should().ContainSingle().Which.GetProperty("paymentId").GetGuid().Should().Be(paid.Id());
    }

    [Fact]
    public async Task Automatic_reconciliation_follows_the_roles_and_branches_of_the_reports()
    {
        await Manager1.GetAsync("/api/v1/payments/reconciliation/auto?branchId=" + B2).ShouldBe(403, "branch.forbidden");
        await Admin.GetAsync("/api/v1/payments/reconciliation/auto?branchId=" + ForeignBranch).ShouldBe(400);
        await Admin.GetAsync("/api/v1/payments/reconciliation/auto?branchId=" + B2).ShouldBe(200);
        await Cashier1.GetAsync("/api/v1/payments/reconciliation/auto").ShouldBe(403);
        await Customer.GetAsync("/api/v1/payments/reconciliation/auto").ShouldBe(403);
        await F.CreateClient().GetAsync("/api/v1/payments/reconciliation/auto").ShouldBe(401);
        await Manager1.GetAsync("/api/v1/payments/reconciliation/auto?from=2026-02-01&to=2026-01-01").ShouldBe(400);
    }
}
