using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;

namespace AICOS.Services.IntegrationTests;

/// <summary>SCRUM-89: reconciling electronic transactions with orders.</summary>
public sealed class PaymentReconciliationTests : PaymentTestBase
{
    private const string Report = "/api/v1/payments/reconciliation";

    private static string[] Codes(JsonElement report) => report.GetProperty("discrepancies").EnumerateArray().Select(d => d.GetProperty("code").GetString()!).ToArray();

    [Fact]
    public async Task The_report_totals_paid_money_per_method_and_status()
    {
        await PaidOnlineAsync(100_000);
        await PaidOnlineAsync(50_000);
        await PaidCashAsync(30_000);
        var refunded = await PaidOnlineAsync(40_000);
        await Cashier1.PostOk($"{Tx}/{refunded.Id()}/refunds", new { kind = "Cancellation", reason = "x" });
        await PayOnlineAsync(amount: 10_000);                                                                    // still pending

        var report = await GetAsync(Admin, $"{Report}?branchId={B1}");

        report.GetProperty("paymentCount").GetInt32().Should().Be(5);
        report.GetProperty("paidCount").GetInt32().Should().Be(4);
        report.GetProperty("gross").GetDecimal().Should().Be(220_000);
        report.GetProperty("refunded").GetDecimal().Should().Be(40_000);
        report.GetProperty("net").GetDecimal().Should().Be(180_000);
        var methods = report.GetProperty("byMethod").EnumerateArray().ToDictionary(m => m.GetProperty("method").GetString()!, m => m.GetProperty("net").GetDecimal());
        methods.Should().BeEquivalentTo(new Dictionary<string, decimal> { ["Cash"] = 30_000, ["Mock"] = 150_000 });
        report.GetProperty("byStatus").GetProperty("Pending").GetInt32().Should().Be(1);
        report.GetProperty("byStatus").GetProperty("Refunded").GetInt32().Should().Be(1);
        Codes(report).Should().BeEmpty();
    }

    [Fact]
    public async Task The_report_flags_payments_that_need_attention()
    {
        F.Clock.Now = new DateTimeOffset(2026, 10, 26, 3, 0, 0, TimeSpan.Zero);                                  // mid-morning in the shop: the test moves the clock 21 minutes and must not cross midnight
        var stale = await PayOnlineAsync(amount: 10_000);
        var lost = await PayOnlineAsync(amount: 20_000);
        await SimulateAsync(lost.Id(), notify: false);                                                          // paid at the provider, never told
        var mismatch = await PayOnlineAsync(amount: 30_000);
        await WebhookAsync("Mock", "x1", mismatch.GetProperty("transactionRef").GetString()!, "success", 1_000);
        var order = Guid.NewGuid();
        var replaced = await PayOnlineAsync(order, 40_000);
        await Cashier1.PostOk($"{Tx}/cash", new { orderId = order, amount = 40_000 });                          // cash wins, the QR is cancelled
        F.Clock.Now = F.Clock.Now.AddMinutes(1);
        await SimulateAsync(replaced.Id());                                                                     // ...but the customer paid the QR as well
        F.Clock.Now = F.Clock.Now.AddMinutes(20);

        var report = await GetAsync(Admin, $"{Report}?branchId={B1}");

        Codes(report).Should().BeEquivalentTo("stale_pending", "stale_pending", "stale_pending", "provider_paid_not_applied", "webhook_amount_mismatch", "paid_late", "duplicate_payment");
        report.GetProperty("discrepancies").EnumerateArray().First(d => d.GetProperty("code").GetString() == "duplicate_payment").GetProperty("paymentId").GetGuid().Should().Be(replaced.Id());
        stale.Id().Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task The_report_counts_refunds_waiting_for_a_decision_and_respects_the_date_range()
    {
        var payment = await PaidOnlineAsync(100_000);
        await Cashier1.PostOk($"{Tx}/{payment.Id()}/refunds", new { kind = "Exception", amount = 5_000, reason = "x" });

        var today = await GetAsync(Admin, $"{Report}?branchId={B1}");
        today.GetProperty("refundsPendingApproval").GetInt32().Should().Be(1);

        var yesterday = DateOnly.FromDateTime(F.Clock.Now.UtcDateTime.AddDays(-2));
        var past = await GetAsync(Admin, $"{Report}?branchId={B1}&from={yesterday:yyyy-MM-dd}&to={yesterday:yyyy-MM-dd}");
        past.GetProperty("paymentCount").GetInt32().Should().Be(0);

        await Admin.GetAsync($"{Report}?from=2026-02-01&to=2026-01-01").ShouldBe(400);
        await Admin.GetAsync($"{Report}?from=2025-01-01&to=2026-01-01").ShouldBe(400);
    }

    [Fact]
    public async Task Access_follows_the_branch_and_brand()
    {
        await PaidOnlineAsync(100_000);

        (await GetAsync(Manager1, Report)).GetProperty("paymentCount").GetInt32().Should().Be(1);
        (await GetAsync(F.As(AicosRoles.BranchManager, B2), Report)).GetProperty("paymentCount").GetInt32().Should().Be(0);
        (await GetAsync(OtherAdmin, Report)).GetProperty("paymentCount").GetInt32().Should().Be(0);
        await Manager1.GetAsync($"{Report}?branchId={B2}").ShouldBe(403, "branch.forbidden");
        await Cashier1.GetAsync(Report).ShouldBe(403);
        await Customer.GetAsync(Report).ShouldBe(403);
        await Super.GetAsync(Report).ShouldBe(403);
    }

    [Fact]
    public async Task Orders_are_compared_with_payments_and_every_disagreement_is_named()
    {
        var ok = Guid.NewGuid();
        var paidNoPayment = Guid.NewGuid();
        var unpaidButPaid = Guid.NewGuid();
        var wrongAmount = Guid.NewGuid();
        var unpaid = Guid.NewGuid();
        var extra = Guid.NewGuid();
        await PaidOnlineAsync(100_000, ok);
        await PaidOnlineAsync(50_000, unpaidButPaid);
        await PaidCashAsync(70_000, wrongAmount);
        await PaidOnlineAsync(25_000, extra);                                                                      // paid but missing from the order list

        var result = await Admin.PostOk($"{Report}/orders", new
        {
            branchId = B1,
            orders = new object[]
            {
                new { orderId = ok, amount = 100_000, isPaid = true },
                new { orderId = paidNoPayment, amount = 10_000, isPaid = true },
                new { orderId = unpaidButPaid, amount = 50_000, isPaid = false },
                new { orderId = wrongAmount, amount = 75_000, isPaid = true },
                new { orderId = unpaid, amount = 20_000, isPaid = false },
            },
        });

        result.GetProperty("orders").GetInt32().Should().Be(5);
        result.GetProperty("matched").GetInt32().Should().Be(2);                                                   // the good one and the genuinely unpaid one
        var found = result.GetProperty("discrepancies").EnumerateArray().ToDictionary(d => d.GetProperty("orderId").GetGuid(), d => d.GetProperty("code").GetString());
        found.Should().BeEquivalentTo(new Dictionary<Guid, string?>
        {
            [paidNoPayment] = "order_paid_without_payment",
            [unpaidButPaid] = "payment_without_paid_order",
            [wrongAmount] = "amount_mismatch",
            [extra] = "payment_without_order",
        });
    }

    [Fact]
    public async Task The_order_comparison_is_validated_and_scoped()
    {
        var id = Guid.NewGuid();
        await Admin.Post($"{Report}/orders", new { }).ShouldBe(400);
        await Admin.Post($"{Report}/orders", new { orders = Array.Empty<object>() }).ShouldBe(400);
        await Admin.Post($"{Report}/orders", new { orders = new[] { new { orderId = id, amount = 1, isPaid = true }, new { orderId = id, amount = 1, isPaid = true } } }).ShouldBe(400);
        await Admin.Post($"{Report}/orders", new { orders = new[] { new { orderId = id, isPaid = true } } }).ShouldBe(400);
        await Cashier1.Post($"{Report}/orders", new { orders = new[] { new { orderId = id, amount = 1, isPaid = true } } }).ShouldBe(403);

        await PaidOnlineAsync(100_000, id);
        var other = await OtherAdmin.PostOk($"{Report}/orders", new { orders = new[] { new { orderId = id, amount = 100_000, isPaid = true } } });
        other.GetProperty("discrepancies")[0].GetProperty("code").GetString().Should().Be("order_paid_without_payment");   // another brand never sees our payment
    }
}
