using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;
using AICOS.CustomerEngagement.Api;
using AICOS.CustomerEngagement.Api.Persistence;

namespace AICOS.Services.IntegrationTests;

/// <summary>The notifications of a member about their orders: created from the order events, read by the member only.</summary>
public sealed class NotificationTests : IDisposable
{
    private const string Notifications = "/api/v1/customers/me/notifications";

    private readonly ServiceFactory<CustomerEngagementService, CustomerEngagementDbContext> _f = new();
    private readonly Guid _tenant = ServiceFactory<CustomerEngagementService, CustomerEngagementDbContext>.DefaultTenantId;
    private readonly Guid _otherTenant = Guid.NewGuid();
    private readonly Guid _b1;
    private readonly Guid _userId = Guid.NewGuid();
    private readonly HttpClient _me;

    public NotificationTests()
    {
        _b1 = _f.Branches.Add(_tenant, name: "Quận 1");
        _me = _f.As(AicosRoles.Customer, userId: _userId);
    }

    public void Dispose() => _f.Dispose();

    private OrderStatusChangedIntegrationEvent Changed(string kind, Guid? order = null, string code = "ORD-1", string? key = null, Guid? user = null, Guid? tenant = null, string? table = null) =>
        new(tenant ?? _tenant, order ?? Guid.NewGuid(), code, _b1, user ?? _userId, kind, key ?? kind, table, 55_000, DateTime.UtcNow);

    private async Task<JsonElement> ListAsync(HttpClient? client = null, string query = "") => await (client ?? _me).GetFromJsonAsync<JsonElement>($"{Notifications}{query}");

    private async Task<int> UnreadAsync(HttpClient? client = null) => (await (client ?? _me).GetFromJsonAsync<JsonElement>($"{Notifications}/unread-count")).GetProperty("unread").GetInt32();

    [Fact]
    public async Task A_member_is_told_when_an_order_is_paid_ready_and_finished()
    {
        var order = Guid.NewGuid();
        await _f.HandleAsync(Changed("Paid", order, "ORD-77"));
        await _f.HandleAsync(Changed("Ready", order, "ORD-77", table: "A1"));
        await _f.HandleAsync(Changed("Completed", order, "ORD-77"));

        var list = await ListAsync();

        list.GetProperty("totalCount").GetInt32().Should().Be(3);
        var items = list.GetProperty("items").EnumerateArray().ToList();
        items.Select(i => i.GetProperty("kind").GetString()).Should().BeEquivalentTo("Paid", "Ready", "Completed");
        var ready = items.Single(i => i.GetProperty("kind").GetString() == "Ready");
        (ready.GetProperty("title").GetString(), ready.GetProperty("orderCode").GetString(), ready.GetProperty("isRead").GetBoolean()).Should().Be(("Đơn đã sẵn sàng", "ORD-77", false));
        ready.GetProperty("body").GetString().Should().Contain("ORD-77").And.Contain("A1");
        (await UnreadAsync()).Should().Be(3);
    }

    [Fact]
    public async Task A_repeated_event_makes_one_notification_but_each_remake_is_new()
    {
        var order = Guid.NewGuid();
        await _f.HandleAsync(Changed("Ready", order));
        await _f.HandleAsync(Changed("Ready", order));
        await _f.HandleAsync(Changed("Remake", order, key: "Remake:1"));
        await _f.HandleAsync(Changed("Remake", order, key: "Remake:1"));
        await _f.HandleAsync(Changed("Remake", order, key: "Remake:2"));

        (await ListAsync()).GetProperty("totalCount").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task Reading_marks_one_or_all_and_the_unread_count_follows()
    {
        await _f.HandleAsync(Changed("Paid"));
        await _f.HandleAsync(Changed("Ready"));
        await _f.HandleAsync(Changed("Cancelled"));
        var first = (await ListAsync()).GetProperty("items")[0].Id();

        var read = await (await _me.Post($"{Notifications}/{first}/read", new { })).Body();
        read.GetProperty("isRead").GetBoolean().Should().BeTrue();
        await _me.Post($"{Notifications}/{first}/read", new { }).ShouldBe(200);                  // reading again changes nothing
        (await UnreadAsync()).Should().Be(2);
        (await ListAsync(query: "?unreadOnly=true")).GetProperty("totalCount").GetInt32().Should().Be(2);

        (await (await _me.Post($"{Notifications}/read-all", new { })).Body()).GetProperty("unread").GetInt32().Should().Be(0);
        (await UnreadAsync()).Should().Be(0);
        (await ListAsync()).GetProperty("totalCount").GetInt32().Should().Be(3);                  // read ones stay in the list
    }

    [Fact]
    public async Task A_member_sees_only_their_own_notifications_in_their_own_brand()
    {
        var someoneElse = _f.As(AicosRoles.Customer, userId: Guid.NewGuid());
        var otherBrand = _f.As(AicosRoles.Customer, userId: _userId, tenantId: _otherTenant);
        await _f.HandleAsync(Changed("Paid"));
        await _f.HandleAsync(Changed("Paid", user: Guid.NewGuid()));
        await _f.HandleAsync(Changed("Paid", tenant: _otherTenant));
        var mine = (await ListAsync()).GetProperty("items")[0].Id();

        (await ListAsync()).GetProperty("totalCount").GetInt32().Should().Be(1);
        (await ListAsync(someoneElse)).GetProperty("totalCount").GetInt32().Should().Be(0);
        (await ListAsync(otherBrand)).GetProperty("totalCount").GetInt32().Should().Be(1);        // the same account in another brand has its own
        await someoneElse.Post($"{Notifications}/{mine}/read", new { }).ShouldBe(404);
        await otherBrand.Post($"{Notifications}/{mine}/read", new { }).ShouldBe(404);
        (await UnreadAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Only_a_signed_in_customer_has_notifications()
    {
        await _f.As(AicosRoles.Cashier, _b1).GetAsync(Notifications).ShouldBe(403);
        await _f.As(AicosRoles.BrandAdmin).GetAsync($"{Notifications}/unread-count").ShouldBe(403);
        await _f.CreateClient().GetAsync(Notifications).ShouldBe(401);
        await _f.CreateClient().Post($"{Notifications}/read-all", new { }).ShouldBe(401);
    }

    [Fact]
    public async Task The_list_is_newest_first_and_paged()
    {
        for (var i = 1; i <= 5; i++)
        {
            _f.Clock.Now = DateTimeOffset.UtcNow.AddMinutes(i);
            await _f.HandleAsync(Changed("Paid", code: $"ORD-{i}"));
        }

        var page = await ListAsync(query: "?page=1&pageSize=2");

        page.GetProperty("items").EnumerateArray().Select(n => n.GetProperty("orderCode").GetString()).Should().Equal("ORD-5", "ORD-4");
        page.GetProperty("totalCount").GetInt32().Should().Be(5);
    }
}
