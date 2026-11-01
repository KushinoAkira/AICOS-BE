using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts;
using AICOS.Contracts.Events;
using AICOS.CustomerEngagement.Api;
using AICOS.CustomerEngagement.Api.Persistence;

namespace AICOS.Services.IntegrationTests;

/// <summary>The notifications of the people working in a brand: made from the events of the other services, each person reads what concerns their role and branch.</summary>
public sealed class StaffNotificationTests : IDisposable
{
    private const string Notifications = "/api/v1/customers/staff/notifications";

    private readonly ServiceFactory<CustomerEngagementService, CustomerEngagementDbContext> _f = new();
    private readonly Guid _tenant = ServiceFactory<CustomerEngagementService, CustomerEngagementDbContext>.DefaultTenantId;
    private readonly Guid _otherTenant = Guid.NewGuid();
    private readonly Guid _b1;
    private readonly Guid _b2;

    public StaffNotificationTests()
    {
        _b1 = _f.Branches.Add(_tenant, name: "Quận 1");
        _b2 = _f.Branches.Add(_tenant, name: "Quận 3");
    }

    public void Dispose() => _f.Dispose();

    private OrderCreatedIntegrationEvent Created(Guid? branch = null, Guid? order = null, string type = "DineIn", bool paid = false, Guid? cashier = null, string? table = "A1") =>
        new(_tenant, order ?? Guid.NewGuid(), "ORD-1", branch ?? _b1, cashier, null, null, table, type, 55_000, paid, null, [new OrderItemDto(Guid.NewGuid(), "Latte", 55_000, 2, null)], DateTime.UtcNow);

    private static async Task<List<JsonElement>> ListAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>(Notifications)).GetProperty("items").EnumerateArray().ToList();

    private static async Task<int> UnreadAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>($"{Notifications}/unread-count")).GetProperty("unread").GetInt32();

    [Fact]
    public async Task An_order_reaches_the_bar_and_the_counter_of_its_branch_only()
    {
        var order = Guid.NewGuid();
        await _f.HandleAsync(Created(order: order));
        await _f.HandleAsync(Created(order: order));   // the same event delivered twice

        var barista = _f.As(AicosRoles.Barista, _b1);
        var cashier = _f.As(AicosRoles.Cashier, _b1);
        var bar = (await ListAsync(barista)).Should().ContainSingle().Subject;
        bar.GetProperty("kind").GetString().Should().Be("NewOrder");
        bar.GetProperty("body").GetString().Should().Contain("Bàn A1").And.Contain("2 món");
        (await ListAsync(cashier)).Should().ContainSingle().Which.GetProperty("kind").GetString().Should().Be("TableOrder");

        (await ListAsync(_f.As(AicosRoles.Barista, _b2))).Should().BeEmpty();                                     // another branch
        (await ListAsync(_f.As(AicosRoles.BranchManager, _b1))).Should().BeEmpty();                               // another role
        (await ListAsync(_f.As(AicosRoles.Barista, _f.Branches.Add(_otherTenant), tenantId: _otherTenant))).Should().BeEmpty();   // another brand
    }

    [Fact]
    public async Task A_takeaway_reaches_the_bar_once_when_it_is_paid()
    {
        var order = Guid.NewGuid();
        var barista = _f.As(AicosRoles.Barista, _b1);

        await _f.HandleAsync(Created(order: order, type: "Takeaway", table: null, cashier: Guid.NewGuid()));
        (await ListAsync(barista)).Should().BeEmpty();

        var paid = new OrderPaidIntegrationEvent(_tenant, order, "ORD-1", _b1, null, 55_000, DateTime.UtcNow);
        await _f.HandleAsync(paid);
        await _f.HandleAsync(paid);
        (await ListAsync(barista)).Should().ContainSingle();
        (await ListAsync(_f.As(AicosRoles.Cashier, _b1))).Should().BeEmpty();   // the counter took this order itself
    }

    [Fact]
    public async Task The_counter_hears_about_ready_orders_bills_and_online_payments_but_not_its_own_cash()
    {
        await _f.HandleAsync(new KitchenOrderReadyIntegrationEvent(_tenant, Guid.NewGuid(), Guid.NewGuid(), "ORD-2", _b1, "B2", DateTime.UtcNow));
        await _f.HandleAsync(new TableBillRequestedIntegrationEvent(_tenant, _b1, Guid.NewGuid(), "A1", "Cash"));
        await _f.HandleAsync(new PaymentSucceededEvent(_tenant, _b1, Guid.NewGuid(), Guid.NewGuid(), 55_000, "VietQR", DateTime.UtcNow));
        await _f.HandleAsync(new PaymentSucceededEvent(_tenant, _b1, Guid.NewGuid(), Guid.NewGuid(), 55_000, "Cash", DateTime.UtcNow));

        (await ListAsync(_f.As(AicosRoles.Cashier, _b1))).Select(n => n.GetProperty("kind").GetString()).Should().BeEquivalentTo(["OrderReady", "BillRequested", "OnlinePaid"]);
    }

    [Fact]
    public async Task The_manager_hears_what_waits_for_a_decision()
    {
        await _f.HandleAsync(new CashClosingSubmittedEvent(_tenant, _b1, Guid.NewGuid(), "Thu Ngân", 100_000, 95_000));
        await _f.HandleAsync(new RefundRequestedEvent(_tenant, _b1, Guid.NewGuid(), Guid.NewGuid(), 20_000, "Sai món"));
        await _f.HandleAsync(new ReplacementRegisteredEvent(_tenant, _b1, Guid.NewGuid(), Guid.NewGuid(), "Pha Chế", new DateOnly(2026, 11, 5)));
        await _f.HandleAsync(new PaymentRefundFailedEvent(_tenant, _b1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Cổng từ chối"));

        var rows = await ListAsync(_f.As(AicosRoles.BranchManager, _b1));
        rows.Select(n => n.GetProperty("kind").GetString()).Should().BeEquivalentTo(["CashClosing", "RefundRequested", "ReplacementRegistered", "RefundFailed"]);
        rows.Single(n => n.GetProperty("kind").GetString() == "CashClosing").GetProperty("body").GetString().Should().Contain("lệch 5.000đ");
        (await ListAsync(_f.As(AicosRoles.BrandAdmin))).Select(n => n.GetProperty("kind").GetString()).Should().BeEquivalentTo(["RefundRequested", "RefundFailed"]);
    }

    [Fact]
    public async Task A_replacement_shift_is_offered_to_the_staff_of_the_branch()
    {
        await _f.HandleAsync(new ReplacementShiftOpenedEvent(_tenant, _b1, Guid.NewGuid(), new DateOnly(2026, 11, 5), new TimeOnly(7, 0), new TimeOnly(12, 0)));

        var row = (await ListAsync(_f.As(AicosRoles.Barista, _b1))).Should().ContainSingle().Subject;
        row.GetProperty("body").GetString().Should().Contain("07:00-12:00").And.Contain("05/11/2026");
        (await ListAsync(_f.As(AicosRoles.Cashier, _b1))).Should().ContainSingle();
    }

    [Fact]
    public async Task The_brand_hears_about_its_plan_and_the_platform_about_new_brands()
    {
        var end = new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc);
        await _f.HandleAsync(new SubscriptionRenewalDueEvent(_tenant, "Demo", "Cơ bản", end, 7));
        await _f.HandleAsync(new SubscriptionOverdueEvent(_tenant, "Demo", "Cơ bản", end, 15, GracePeriodOver: true));
        await _f.HandleAsync(new BrandRegisteredEvent(Guid.NewGuid(), "Cà phê mới", "ca-phe-moi", "owner@example.com"));

        (await ListAsync(_f.As(AicosRoles.BrandAdmin))).Select(n => n.GetProperty("kind").GetString()).Should().BeEquivalentTo(["RenewalDue", "Overdue"]);
        (await ListAsync(_f.As(AicosRoles.SuperAdmin))).Select(n => n.GetProperty("kind").GetString()).Should().BeEquivalentTo(["Overdue", "BrandRegistered"]);
    }

    [Fact]
    public async Task Reading_is_personal_even_though_the_notification_is_shared()
    {
        await _f.HandleAsync(Created());
        await _f.HandleAsync(Created());
        var first = _f.As(AicosRoles.Barista, _b1, Guid.NewGuid());
        var second = _f.As(AicosRoles.Barista, _b1, Guid.NewGuid());
        (await UnreadAsync(first)).Should().Be(2);

        var id = (await ListAsync(first))[0].Id();
        await first.Post($"{Notifications}/{id}/read", new { }).ShouldBe(204);
        await first.Post($"{Notifications}/{id}/read", new { }).ShouldBe(204);   // twice is fine
        (await UnreadAsync(first)).Should().Be(1);
        (await UnreadAsync(second)).Should().Be(2);
        (await ListAsync(first)).Single(n => n.Id() == id).GetProperty("isRead").GetBoolean().Should().BeTrue();

        (await first.GetFromJsonAsync<JsonElement>($"{Notifications}?unreadOnly=true")).GetProperty("totalCount").GetInt32().Should().Be(1);
        await first.Post($"{Notifications}/read-all", new { }).ShouldBe(200);
        (await UnreadAsync(first)).Should().Be(0);
        (await UnreadAsync(second)).Should().Be(2);

        await _f.As(AicosRoles.Cashier, _b2).Post($"{Notifications}/{id}/read", new { }).ShouldBe(404);   // not theirs
    }

    [Fact]
    public async Task Customers_and_anonymous_callers_have_no_staff_inbox()
    {
        await _f.As(AicosRoles.Customer).GetAsync(Notifications).ShouldBe(403);
        await _f.CreateClient().GetAsync(Notifications).ShouldBe(401);
    }
}
