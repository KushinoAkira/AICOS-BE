using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;

namespace AICOS.Services.IntegrationTests;

/// <summary>SCRUM-88: refund request and result, inside the brand policy and as exceptions.</summary>
public sealed class PaymentRefundTests : PaymentTestBase
{
    private Task SetPolicyAsync(int cancel = 100, int noShowRefund = 0, bool auto = true, string approver = "BranchManager") =>
        Admin.Put(Policy, new { cancelRefundPercent = cancel, noShowAfterMinutes = 30, noShowRefundPercent = noShowRefund, unpaidOrderTimeoutMinutes = 15, autoApproveWithinPolicy = auto, exceptionApprover = approver }).ShouldBe(200);

    private string RefundsOf(JsonElement payment) => $"{Tx}/{payment.Id()}/refunds";

    [Fact]
    public async Task A_cancellation_inside_the_policy_is_refunded_at_once()
    {
        var order = Guid.NewGuid();
        var payment = await PaidOnlineAsync(80_000, order);

        var response = await Cashier1.Post(RefundsOf(payment), new { kind = "Cancellation", reason = "Customer changed their mind" });

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Created);
        var refund = await response.Body();
        (refund.GetProperty("status").GetString(), refund.GetProperty("amount").GetDecimal(), refund.GetProperty("withinPolicy").GetBoolean()).Should().Be(("Succeeded", 80_000m, true));
        var read = await GetAsync(Cashier1, $"{Tx}/{payment.Id()}");
        read.GetProperty("status").GetString().Should().Be("Refunded");
        read.GetProperty("refundedAmount").GetDecimal().Should().Be(80_000);
        read.GetProperty("refunds").GetArrayLength().Should().Be(1);
        var ev = F.Events.Published.OfType<PaymentRefundedEvent>().Single();
        (ev.OrderId, ev.Amount, ev.TotalRefunded, ev.Method).Should().Be((order, 80_000m, 80_000m, "Mock"));
        F.Events.Published.OfType<CashPaymentRecordedEvent>().Should().BeEmpty();                           // an online refund does not touch the till
    }

    [Fact]
    public async Task A_cash_refund_takes_money_out_of_the_till_of_the_person_who_paid_it()
    {
        var payment = await PaidCashAsync(60_000);

        await Cashier1.Post(RefundsOf(payment), new { kind = "Cancellation", reason = "Wrong order" }).ShouldBe(201);

        var cash = F.Events.Published.OfType<CashPaymentRecordedEvent>().Last();
        (cash.Amount, cash.CashierUserId, cash.BranchId).Should().Be((-60_000m, CashierId, B1));
        F.Events.Published.OfType<CashPaymentRecordedEvent>().Should().HaveCount(2);
    }

    [Fact]
    public async Task The_policy_percentage_limits_the_automatic_refund_and_more_than_that_needs_approval()
    {
        await SetPolicyAsync(cancel: 50);
        var payment = await PaidOnlineAsync(100_000);

        var auto = await Cashier1.PostOk(RefundsOf(payment), new { kind = "Cancellation", reason = "Cancelled" });          // no amount: the share the policy allows
        auto.GetProperty("amount").GetDecimal().Should().Be(50_000);
        auto.GetProperty("status").GetString().Should().Be("Succeeded");
        (await GetAsync(Cashier1, $"{Tx}/{payment.Id()}")).GetProperty("status").GetString().Should().Be("PartiallyRefunded");

        await Cashier1.Post(RefundsOf(payment), new { kind = "Cancellation", reason = "Again" }).ShouldBe(422, "refund.nothing_to_refund");

        var extra = await Cashier1.PostOk(RefundsOf(payment), new { kind = "Cancellation", amount = 20_000, reason = "Goodwill" });
        extra.GetProperty("status").GetString().Should().Be("PendingApproval");
        extra.GetProperty("withinPolicy").GetBoolean().Should().BeFalse();
        (await GetAsync(Cashier1, $"{Tx}/{payment.Id()}")).GetProperty("refundedAmount").GetDecimal().Should().Be(50_000);   // nothing paid out yet

        var approved = await Manager1.PostOk($"{Refunds}/{extra.Id()}/approve", new { });

        approved.GetProperty("status").GetString().Should().Be("Succeeded");
        approved.GetProperty("decidedBy").GetGuid().Should().Be(ManagerId);
        (await GetAsync(Cashier1, $"{Tx}/{payment.Id()}")).GetProperty("refundedAmount").GetDecimal().Should().Be(70_000);
        F.Events.Published.OfType<PaymentRefundedEvent>().Select(e => e.TotalRefunded).Should().Equal(50_000m, 70_000m);
    }

    [Fact]
    public async Task A_no_show_follows_its_own_percentage()
    {
        await SetPolicyAsync(noShowRefund: 0);
        var none = await PaidOnlineAsync(100_000);
        await Cashier1.Post(RefundsOf(none), new { kind = "NoShow", reason = "Nobody came" }).ShouldBe(422, "refund.nothing_to_refund");

        await SetPolicyAsync(noShowRefund: 30);
        var some = await PaidOnlineAsync(100_000);

        var refund = await Cashier1.PostOk(RefundsOf(some), new { kind = "NoShow", reason = "Nobody came" });
        (refund.GetProperty("amount").GetDecimal(), refund.GetProperty("status").GetString()).Should().Be((30_000m, "Succeeded"));
    }

    [Fact]
    public async Task An_exception_always_waits_for_the_approver_and_staff_cannot_approve_their_own_request()
    {
        var payment = await PaidOnlineAsync(100_000);

        await Cashier1.Post(RefundsOf(payment), new { kind = "Exception", reason = "Cold drink" }).ShouldBe(400);                // exceptions need an amount
        var request = await Manager1.PostOk(RefundsOf(payment), new { kind = "Exception", amount = 30_000, reason = "Cold drink" });
        request.GetProperty("status").GetString().Should().Be("PendingApproval");
        F.Events.Published.OfType<RefundRequestedEvent>().Should().ContainSingle(e => e.RefundId == request.Id() && e.Amount == 30_000);

        await Manager1.Post($"{Refunds}/{request.Id()}/approve", new { }).ShouldBe(403, "refund.self_approval");
        await Cashier1.Post($"{Refunds}/{request.Id()}/approve", new { }).ShouldBe(403);
        await Customer.Post($"{Refunds}/{request.Id()}/approve", new { }).ShouldBe(403);

        var other = F.As(AicosRoles.BranchManager, B1);
        (await other.PostOk($"{Refunds}/{request.Id()}/approve", new { })).GetProperty("status").GetString().Should().Be("Succeeded");
        await other.Post($"{Refunds}/{request.Id()}/approve", new { }).ShouldBe(422, "refund.not_pending");
    }

    [Fact]
    public async Task When_the_brand_wants_the_brand_admin_to_decide_a_branch_manager_cannot()
    {
        await SetPolicyAsync(approver: "BrandAdmin");
        var payment = await PaidOnlineAsync(100_000);
        var request = await Cashier1.PostOk(RefundsOf(payment), new { kind = "Exception", amount = 10_000, reason = "Spilled" });

        await Manager1.Post($"{Refunds}/{request.Id()}/approve", new { }).ShouldBe(403, "refund.approver_required");
        await Manager1.Post($"{Refunds}/{request.Id()}/reject", new { note = "no" }).ShouldBe(403, "refund.approver_required");

        (await Admin.PostOk($"{Refunds}/{request.Id()}/approve", new { })).GetProperty("status").GetString().Should().Be("Succeeded");
    }

    [Fact]
    public async Task With_automatic_approval_off_every_refund_waits()
    {
        await SetPolicyAsync(auto: false);
        var payment = await PaidOnlineAsync(100_000);

        var request = await Cashier1.PostOk(RefundsOf(payment), new { kind = "Cancellation", reason = "Cancelled" });

        (request.GetProperty("status").GetString(), request.GetProperty("withinPolicy").GetBoolean()).Should().Be(("PendingApproval", true));
        (await GetAsync(Cashier1, $"{Tx}/{payment.Id()}")).GetProperty("status").GetString().Should().Be("Succeeded");
    }

    [Fact]
    public async Task A_rejected_refund_pays_nothing_and_needs_a_note()
    {
        var payment = await PaidOnlineAsync(100_000);
        var request = await Cashier1.PostOk(RefundsOf(payment), new { kind = "Exception", amount = 10_000, reason = "Complaint" });

        await Manager1.Post($"{Refunds}/{request.Id()}/reject", new { }).ShouldBe(400);
        var rejected = await Manager1.PostOk($"{Refunds}/{request.Id()}/reject", new { note = "Drink was fine" });

        (rejected.GetProperty("status").GetString(), rejected.GetProperty("decisionNote").GetString()).Should().Be(("Rejected", "Drink was fine"));
        await Manager1.Post($"{Refunds}/{request.Id()}/approve", new { }).ShouldBe(422, "refund.not_pending");
        (await GetAsync(Cashier1, $"{Tx}/{payment.Id()}")).GetProperty("refundedAmount").GetDecimal().Should().Be(0);
        F.Events.Published.OfType<PaymentRefundedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task Refunds_cannot_exceed_the_payment_or_be_made_on_unpaid_payments()
    {
        var payment = await PaidOnlineAsync(100_000);
        await Cashier1.Post(RefundsOf(payment), new { kind = "Exception", amount = 100_001, reason = "Too much" }).ShouldBe(422, "refund.exceeds_payment");

        var held = await Cashier1.PostOk(RefundsOf(payment), new { kind = "Exception", amount = 80_000, reason = "First" });
        await Cashier1.Post(RefundsOf(payment), new { kind = "Exception", amount = 30_000, reason = "Second" }).ShouldBe(422, "refund.exceeds_payment");   // 80,000 is already reserved
        await Cashier1.PostOk(RefundsOf(payment), new { kind = "Exception", amount = 20_000, reason = "Rest" });
        await Cashier1.Post(RefundsOf(payment), new { kind = "Exception", amount = 1_000, reason = "Nothing left" }).ShouldBe(422, "refund.nothing_to_refund");
        await Manager1.PostOk($"{Refunds}/{held.Id()}/approve", new { });

        var pending = await PayOnlineAsync(amount: 10_000);
        await Cashier1.Post(RefundsOf(pending), new { kind = "Cancellation", reason = "Not paid yet" }).ShouldBe(422, "payment.not_refundable");
    }

    [Fact]
    public async Task A_customer_asks_for_the_refund_of_their_own_online_payment_only_by_the_brand_policy()
    {
        await SetPolicyAsync(cancel: 50);
        var payment = await PaidOnlineAsync(100_000);                                  // paid by the customer

        var refund = await Customer.PostOk(RefundsOf(payment), new { kind = "Cancellation", reason = "Bận việc" });

        (refund.GetProperty("amount").GetDecimal(), refund.GetProperty("status").GetString(), refund.GetProperty("withinPolicy").GetBoolean()).Should().Be((50_000m, "Succeeded", true));
        await Customer.Post(RefundsOf(payment), new { kind = "Cancellation", reason = "Again" }).ShouldBe(422, "refund.nothing_to_refund");
        await Customer.GetAsync($"{Refunds}").ShouldBe(403);                           // the refund list stays for the counter
    }

    [Fact]
    public async Task A_customer_cannot_refund_a_payment_taken_at_the_counter()
    {
        var cash = await PaidCashAsync(60_000);                                        // the cashier took it

        await Customer.Post(RefundsOf(cash), new { kind = "Cancellation", reason = "x" }).ShouldBe(404);
    }

    [Fact]
    public async Task An_order_the_shop_could_not_make_is_refunded_in_full_whatever_the_cancellation_policy_says_and_the_bar_may_ask_for_it()
    {
        await SetPolicyAsync(cancel: 0);
        var payment = await PaidOnlineAsync(100_000);
        var barista = F.As(AicosRoles.Barista, B1);

        var refund = await barista.PostOk(RefundsOf(payment), new { kind = "Unfulfillable", reason = "Hết sữa" });

        (refund.GetProperty("amount").GetDecimal(), refund.GetProperty("status").GetString(), refund.GetProperty("withinPolicy").GetBoolean()).Should().Be((100_000m, "Succeeded", true));
        (await GetAsync(Cashier1, $"{Tx}/{payment.Id()}")).GetProperty("status").GetString().Should().Be("Refunded");
    }

    [Fact]
    public async Task The_bar_can_only_ask_for_the_refund_of_an_unfulfillable_order_in_its_own_branch()
    {
        var payment = await PaidOnlineAsync(100_000);

        await F.As(AicosRoles.Barista, B1).Post(RefundsOf(payment), new { kind = "Cancellation", reason = "x" }).ShouldBe(403, "refund.bar_unfulfillable_only");
        await F.As(AicosRoles.Barista, B1).Post(RefundsOf(payment), new { kind = "Exception", amount = 1000, reason = "x" }).ShouldBe(403, "refund.bar_unfulfillable_only");
        await F.As(AicosRoles.Barista, B2).Post(RefundsOf(payment), new { kind = "Unfulfillable", reason = "x" }).ShouldBe(403, "branch.forbidden");
        await Customer.Post(RefundsOf(payment), new { kind = "Unfulfillable", reason = "x" }).ShouldBe(403, "refund.customer_policy_only");
        await Cashier1.Post(RefundsOf(payment), new { kind = "Unfulfillable", reason = "Hết ly" }).ShouldBe(201);       // the counter may too
    }

    [Fact]
    public async Task Refund_requests_are_validated_and_limited_to_counter_staff_of_the_branch_and_brand()
    {
        var payment = await PaidOnlineAsync(100_000);

        await Cashier1.Post(RefundsOf(payment), new { }).ShouldBe(400);
        await Cashier1.Post(RefundsOf(payment), new { kind = "Cancellation" }).ShouldBe(400);
        await Cashier1.Post(RefundsOf(payment), new { kind = "Cancellation", reason = "x", amount = -1 }).ShouldBe(400);
        await Cashier1.Post(RefundsOf(payment), new { kind = "Cancellation", reason = "x", amount = 10.5 }).ShouldBe(400);
        await Customer.Post(RefundsOf(payment), new { kind = "Exception", amount = 10_000, reason = "x" }).ShouldBe(403, "refund.customer_policy_only");
        await Customer.Post(RefundsOf(payment), new { kind = "Cancellation", amount = 10_000, reason = "x" }).ShouldBe(403, "refund.customer_policy_only");
        await Customer.Post(RefundsOf(payment), new { kind = "NoShow", reason = "x" }).ShouldBe(403, "refund.customer_policy_only");
        await F.As(AicosRoles.Customer, userId: Guid.NewGuid()).Post(RefundsOf(payment), new { kind = "Cancellation", reason = "x" }).ShouldBe(404);   // not their payment
        await F.As(AicosRoles.Barista, B1).Post(RefundsOf(payment), new { kind = "Cancellation", reason = "x" }).ShouldBe(403);
        await Cashier2.Post(RefundsOf(payment), new { kind = "Cancellation", reason = "x" }).ShouldBe(403, "branch.forbidden");
        await OtherAdmin.Post(RefundsOf(payment), new { kind = "Cancellation", reason = "x" }).ShouldBe(404);
        await F.CreateClient().Post(RefundsOf(payment), new { kind = "Cancellation", reason = "x" }).ShouldBe(401);
    }

    [Fact]
    public async Task Pending_refunds_are_listed_for_the_approver_of_the_branch_only()
    {
        var p1 = await PaidOnlineAsync(100_000);
        await Cashier1.PostOk(RefundsOf(p1), new { kind = "Exception", amount = 10_000, reason = "A" });
        var p2 = await PaidCashAsync(50_000);
        await Cashier1.PostOk(RefundsOf(p2), new { kind = "Cancellation", reason = "B" });                                       // automatic

        (await GetAsync(Manager1, $"{Refunds}?status=PendingApproval")).GetArrayLength().Should().Be(1);
        (await GetAsync(Admin, Refunds)).GetArrayLength().Should().Be(2);
        (await GetAsync(Admin, $"{Refunds}?paymentId={p1.Id()}")).GetArrayLength().Should().Be(1);
        (await GetAsync(F.As(AicosRoles.BranchManager, B2), Refunds)).GetArrayLength().Should().Be(0);
        (await GetAsync(OtherAdmin, Refunds)).GetArrayLength().Should().Be(0);
        await Manager1.GetAsync($"{Refunds}?branchId={B2}").ShouldBe(403, "branch.forbidden");
        await Customer.GetAsync(Refunds).ShouldBe(403);

        var one = (await GetAsync(Admin, $"{Refunds}?status=PendingApproval"))[0];
        await Manager1.GetAsync($"{Refunds}/{one.Id()}").ShouldBe(200);
        await F.As(AicosRoles.BranchManager, B2).GetAsync($"{Refunds}/{one.Id()}").ShouldBe(403, "branch.forbidden");
        await OtherAdmin.GetAsync($"{Refunds}/{one.Id()}").ShouldBe(404);
    }

    [Fact]
    public async Task Every_step_of_a_refund_goes_to_the_audit_history()
    {
        var payment = await PaidOnlineAsync(100_000);
        var held = await Cashier1.PostOk(RefundsOf(payment), new { kind = "Exception", amount = 10_000, reason = "A" });
        await Manager1.PostOk($"{Refunds}/{held.Id()}/approve", new { });
        var auto = await PaidOnlineAsync(50_000);
        await Cashier1.PostOk(RefundsOf(auto), new { kind = "Cancellation", reason = "B" });
        var rejected = await Cashier1.PostOk(RefundsOf(await PaidOnlineAsync(40_000)), new { kind = "Exception", amount = 5_000, reason = "C" });
        await Manager1.PostOk($"{Refunds}/{rejected.Id()}/reject", new { note = "no" });

        F.Events.Published.OfType<AuditRecordedEvent>().Where(e => e.EntityType == "PaymentRefund").Select(e => e.Action).Should().Equal(
            "refund.requested", "refund.approved", "refund.requested", "refund.auto_approved", "refund.requested", "refund.rejected");
    }
}
