using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;
using AICOS.Ordering.Api;
using AICOS.Ordering.Api.Persistence;
using AICOS.Ordering.Api.Services;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AICOS.Services.IntegrationTests;

/// <summary>A customer screen follows the order live: a guest at a table with the session token, a member with their own token (replaces polling).</summary>
public sealed class OrderTrackingTests : IDisposable
{
    private const string Orders = "/api/v1/orders";
    private const string Qr = "/api/v1/orders/qr";
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 3, 0, 0, TimeSpan.Zero);

    private readonly FakeMenu _menu = new();
    private readonly FakeTableQr _tables = new();
    private readonly FakePayments _payments = new();
    private readonly ServiceFactory<OrderingService, OrderingDbContext> _f;
    private readonly Guid _tenant = ServiceFactory<OrderingService, OrderingDbContext>.DefaultTenantId;
    private readonly Guid _b1;
    private readonly Guid _latte;
    private readonly string _t1;
    private readonly string _t2;
    private readonly List<HubConnection> _connections = [];

    public OrderTrackingTests()
    {
        _f = new ServiceFactory<OrderingService, OrderingDbContext>(services =>
        {
            services.RemoveAll<IMenuCatalogClient>();
            services.AddSingleton<IMenuCatalogClient>(_menu);
            services.RemoveAll<ITableQrClient>();
            services.AddSingleton<ITableQrClient>(_tables);
            services.RemoveAll<IPaymentClient>();
            services.AddSingleton<IPaymentClient>(_payments);
        });
        _f.Clock.Now = Now;
        _b1 = _f.Branches.Add(_tenant, name: "Quận 1");
        _latte = _menu.Add(_b1, "Latte", 55_000);
        _t1 = _tables.Add(_tenant, _b1, "A1");
        _t2 = _tables.Add(_tenant, _b1, "A2");
        _f.CreateClient();                                  // starts the test server
    }

    public void Dispose()
    {
        foreach (var connection in _connections)
        {
            connection.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        _f.Dispose();
    }

    private sealed class Screen
    {
        public ConcurrentQueue<JsonElement> Orders { get; } = new();

        public ConcurrentQueue<JsonElement> Closed { get; } = new();

        public async Task<List<string>> KindsAsync(int expected)
        {
            for (var i = 0; i < 100 && Orders.Count < expected; i++)
            {
                await Task.Delay(50);
            }

            return Orders.Select(m => m.GetProperty("kind").GetString()!).ToList();
        }
    }

    private async Task<Screen> ConnectAsync(string? sessionToken = null, string? jwt = null)
    {
        var url = "http://localhost/hubs/orders" + (sessionToken is null ? string.Empty : $"?sessionToken={sessionToken}");
        var connection = new HubConnectionBuilder().WithUrl(url, o =>
        {
            o.HttpMessageHandlerFactory = _ => _f.Server.CreateHandler();
            o.Transports = HttpTransportType.LongPolling;
            if (jwt is not null)
            {
                o.AccessTokenProvider = () => Task.FromResult<string?>(jwt);
            }
        }).Build();
        var screen = new Screen();
        connection.On<JsonElement>("OrderChanged", m => screen.Orders.Enqueue(m));
        connection.On<JsonElement>("SessionClosed", m => screen.Closed.Enqueue(m));
        _connections.Add(connection);
        await connection.StartAsync();
        return screen;
    }

    private static string TokenOf(HttpClient client) => client.DefaultRequestHeaders.Authorization!.Parameter!;

    private async Task<(string Token, HttpClient Phone)> ScanAsync(string code)
    {
        var scan = await (await _f.CreateClient().Post($"{Qr}/scan", new { qrToken = code })).Body();
        var token = scan.GetProperty("sessionToken").GetString()!;
        var phone = _f.CreateClient();
        phone.DefaultRequestHeaders.Add("X-Table-Session", token);
        return (token, phone);
    }

    private async Task<JsonElement> OrderAtTableAsync(HttpClient phone)
    {
        await phone.PostOk($"{Qr}/cart/items", new { productId = _latte, quantity = 1 });
        return await (await phone.Post($"{Qr}/checkout", new { })).Body();
    }

    private PaymentSucceededEvent Paid(JsonElement order) => new(_tenant, _b1, Guid.NewGuid(), order.Id(), order.GetProperty("finalAmount").GetDecimal(), "Cash", Now.UtcDateTime);

    private Task BarAsync(JsonElement order, string step) => step switch
    {
        "start" => _f.HandleAsync(new KitchenOrderStartedIntegrationEvent(_tenant, Guid.NewGuid(), order.Id(), _b1, Now.UtcDateTime)),
        "ready" => _f.HandleAsync(new KitchenOrderReadyIntegrationEvent(_tenant, Guid.NewGuid(), order.Id(), "X", _b1, "A1", Now.UtcDateTime)),
        _ => _f.HandleAsync(new KitchenOrderRemakeIntegrationEvent(_tenant, Guid.NewGuid(), order.Id(), _b1, "Đổ ly", 1, Now.UtcDateTime)),
    };

    [Fact]
    public async Task A_guest_at_the_table_follows_the_order_live_and_is_told_when_the_sitting_ends()
    {
        var (token, phone) = await ScanAsync(_t1);
        var screen = await ConnectAsync(token);

        var order = await OrderAtTableAsync(phone);
        await BarAsync(order, "start");
        await BarAsync(order, "ready");
        await _f.HandleAsync(Paid(order));

        (await screen.KindsAsync(4)).Should().Equal("Created", "Preparing", "Ready", "Paid");
        var ready = screen.Orders.Single(m => m.GetProperty("kind").GetString() == "Ready");
        (ready.GetProperty("orderCode").GetString(), ready.GetProperty("status").GetString(), ready.GetProperty("title").GetString()).Should().Be((order.GetProperty("orderCode").GetString(), "Ready", "Đơn đã sẵn sàng"));
        ready.GetProperty("message").GetString().Should().Contain("A1");
        for (var i = 0; i < 100 && screen.Closed.IsEmpty; i++)
        {
            await Task.Delay(50);
        }

        screen.Closed.Single().GetProperty("reason").GetString().Should().Be("Paid");                  // every round is paid: the session ended
    }

    [Fact]
    public async Task A_remade_order_shows_as_preparing_again_on_the_table_screen()
    {
        var (token, phone) = await ScanAsync(_t1);
        var screen = await ConnectAsync(token);
        var order = await OrderAtTableAsync(phone);
        await BarAsync(order, "ready");

        await BarAsync(order, "remake");

        (await screen.KindsAsync(3)).Should().Equal("Created", "Ready", "Remake");
        var remake = screen.Orders.Last();
        (remake.GetProperty("status").GetString(), remake.GetProperty("remakeCount").GetInt32()).Should().Be(("Preparing", 1));
    }

    [Fact]
    public async Task A_guest_hears_only_about_their_own_table()
    {
        var (token1, phone1) = await ScanAsync(_t1);
        var (token2, phone2) = await ScanAsync(_t2);
        var screen1 = await ConnectAsync(token1);
        var screen2 = await ConnectAsync(token2);

        var order1 = await OrderAtTableAsync(phone1);
        var order2 = await OrderAtTableAsync(phone2);

        (await screen1.KindsAsync(1)).Should().Equal("Created");
        (await screen2.KindsAsync(1)).Should().Equal("Created");
        await Task.Delay(300);
        screen1.Orders.Single().GetProperty("orderId").GetGuid().Should().Be(order1.Id());
        screen2.Orders.Single().GetProperty("orderId").GetGuid().Should().Be(order2.Id());
    }

    [Fact]
    public async Task A_member_follows_all_their_orders_and_is_told_what_the_notification_list_will_keep()
    {
        var memberId = Guid.NewGuid();
        var member = _f.As(AicosRoles.Customer, userId: memberId);
        var screen = await ConnectAsync(jwt: TokenOf(member));
        var other = await ConnectAsync(jwt: TokenOf(_f.As(AicosRoles.Customer, userId: Guid.NewGuid())));
        var slot = new DateTime(2026, 10, 26, 4, 0, 0, DateTimeKind.Utc);
        var order = await (await member.Post($"{Orders}/pickup", new { branchId = _b1, slotStartUtc = slot, items = new object[] { new { productId = _latte, quantity = 1 } } })).Body();

        await _f.HandleAsync(Paid(order));
        await BarAsync(order, "start");
        await BarAsync(order, "ready");
        await BarAsync(order, "remake");

        (await screen.KindsAsync(5)).Should().Equal("Created", "Paid", "Preparing", "Ready", "Remake");
        other.Orders.Should().BeEmpty();
        _f.Events.Published.OfType<OrderStatusChangedIntegrationEvent>().Select(e => (e.Kind, e.DedupeKey)).Should().Equal(("Paid", "Paid"), ("Ready", "Ready"), ("Remake", "Remake:1"));
        _f.Events.Published.OfType<OrderStatusChangedIntegrationEvent>().Should().OnlyContain(e => e.CustomerId == memberId && e.OrderId == order.Id());
    }

    [Fact]
    public async Task A_member_is_told_when_the_order_is_closed_as_a_no_show_or_unfulfillable()
    {
        var member = _f.As(AicosRoles.Customer, userId: Guid.NewGuid());
        var screen = await ConnectAsync(jwt: TokenOf(member));
        var cashier = _f.As(AicosRoles.Cashier, _b1);
        var slot = new DateTime(2026, 10, 26, 4, 0, 0, DateTimeKind.Utc);
        var late = await (await member.Post($"{Orders}/pickup", new { branchId = _b1, slotStartUtc = slot, items = new object[] { new { productId = _latte, quantity = 1 } } })).Body();
        var broken = await (await member.Post($"{Orders}/pickup", new { branchId = _b1, slotStartUtc = slot.AddMinutes(15), items = new object[] { new { productId = _latte, quantity = 1 } } })).Body();

        await cashier.Post($"{Orders}/{broken.Id()}/unfulfillable", new { reason = "Hết sữa" }).ShouldBe(200);
        _f.Clock.Now = new DateTimeOffset(slot.AddMinutes(31));
        await cashier.Post($"{Orders}/{late.Id()}/no-show", new { reason = "Không đến" }).ShouldBe(200);

        (await screen.KindsAsync(4)).Should().Equal("Created", "Created", "Unfulfillable", "NoShow");
        _f.Events.Published.OfType<OrderStatusChangedIntegrationEvent>().Select(e => e.Kind).Should().Equal("Unfulfillable", "NoShow");
        _f.Events.Published.OfType<OrderCancelledIntegrationEvent>().Select(e => e.ClosedAs).Should().Equal("Unfulfillable", "NoShow");
    }

    [Fact]
    public async Task A_walk_in_order_without_a_member_sends_no_notification_event()
    {
        var order = await _f.As(AicosRoles.Cashier, _b1).PostOk(Orders, new { type = "Takeaway", items = new object[] { new { productId = _latte, quantity = 1 } } });
        await _f.HandleAsync(Paid(order));

        _f.Events.Published.OfType<OrderStatusChangedIntegrationEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task Without_a_valid_session_token_or_a_customer_sign_in_the_server_closes_the_connection()
    {
        var (token, _) = await ScanAsync(_t1);

        await ConnectAsync();
        var anonymous = _connections.Last();
        await ConnectAsync(sessionToken: token[..^3] + "abc");
        var forged = _connections.Last();
        await ConnectAsync(sessionToken: $"{Guid.NewGuid():N}.AAAA");
        var unknownSession = _connections.Last();
        await ConnectAsync(jwt: TokenOf(_f.As(AicosRoles.Cashier, _b1)));
        var staff = _connections.Last();
        await ConnectAsync(sessionToken: token);
        var valid = _connections.Last();

        for (var i = 0; i < 100 && new[] { anonymous, forged, unknownSession, staff }.Any(c => c.State != HubConnectionState.Disconnected); i++)
        {
            await Task.Delay(50);
        }

        new[] { anonymous, forged, unknownSession, staff }.Should().OnlyContain(c => c.State == HubConnectionState.Disconnected);
        valid.State.Should().Be(HubConnectionState.Connected);
    }
}
