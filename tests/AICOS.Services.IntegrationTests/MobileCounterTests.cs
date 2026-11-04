using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Ordering.Api;
using AICOS.Ordering.Api.Persistence;
using AICOS.Ordering.Api.Services;
using AICOS.Ordering.Api.Features.Qr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AICOS.Services.IntegrationTests;

/// <summary>HTTP contract integration with EF InMemory and fake service clients, not PostgreSQL concurrency tests.</summary>
public sealed class MobileCounterTests : IDisposable
{
    private readonly FakeMenu _menu = new();
    private readonly FakeTableQr _tables = new();
    private readonly ServiceFactory<OrderingService, OrderingDbContext> _factory;
    private readonly Guid _tenant = ServiceFactory<OrderingService, OrderingDbContext>.DefaultTenantId;
    private readonly Guid _branch;
    private readonly Guid _cashier = Guid.NewGuid();
    private readonly Guid _product;

    public MobileCounterTests()
    {
        _factory = new(services =>
        {
            services.RemoveAll<IMenuCatalogClient>();
            services.AddSingleton<IMenuCatalogClient>(_menu);
            services.RemoveAll<ITableQrClient>();
            services.AddSingleton<ITableQrClient>(_tables);
        });
        _branch = _factory.Branches.Add(_tenant, name: "Mobile test branch");
        _product = _menu.Add(_branch, "Coffee", 50_000);
    }

    public void Dispose() => _factory.Dispose();

    private object Body(int quantity = 1) => new { branchId = _branch, type = "Takeaway", items = new[] { new { productId = _product, quantity } } };
    private async Task<HttpClient> CashierAsync(string key)
    {
        var client = _factory.As(AicosRoles.Cashier, _branch, _cashier);
        client.DefaultRequestHeaders.Add("Idempotency-Key", key);
        var capabilities = await client.GetFromJsonAsync<JsonElement>("/api/v1/orders/staff/capabilities");
        capabilities.GetProperty("counterIdempotency").GetBoolean().Should().BeTrue();
        return client;
    }

    [Fact]
    public async Task Replaying_the_same_counter_request_returns_the_same_order()
    {
        using var client = await CashierAsync("mobile-replay");
        var first = await client.PostAsJsonAsync("/api/v1/orders", Body());
        first.StatusCode.Should().Be(System.Net.HttpStatusCode.Created);
        var created = await first.Content.ReadFromJsonAsync<JsonElement>();
        var second = await client.PostAsJsonAsync("/api/v1/orders", Body());
        second.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var replay = await second.Content.ReadFromJsonAsync<JsonElement>();
        replay.GetProperty("id").GetGuid().Should().Be(created.GetProperty("id").GetGuid());
        var list = await client.GetFromJsonAsync<JsonElement>("/api/v1/orders?includePaid=true");
        list.GetArrayLength().Should().Be(1);
        list[0].GetProperty("cashierId").GetGuid().Should().Be(_cashier);
    }

    [Fact]
    public async Task The_same_key_cannot_create_a_different_order()
    {
        using var client = await CashierAsync("mobile-conflict");
        (await client.PostAsJsonAsync("/api/v1/orders", Body())).EnsureSuccessStatusCode();
        var changed = await client.PostAsJsonAsync("/api/v1/orders", Body(2));
        changed.StatusCode.Should().Be(System.Net.HttpStatusCode.Conflict);
        (await changed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString().Should().Be("order.request_key_conflict");
    }

    [Fact]
    public async Task Counter_rounds_and_guest_QR_join_one_verified_table_session()
    {
        var qr = _tables.Add(_tenant, _branch, "S02");
        var table = (await _tables.LookupAsync(qr, default))!;
        var body = new { branchId = _branch, type = "DineIn", tableId = table.TableId, tableNumber = table.TableCode,
            tableQrToken = qr, items = new[] { new { productId = _product, quantity = 1 } } };
        using var firstClient = await CashierAsync("round-one");
        var first = await (await firstClient.PostAsJsonAsync("/api/v1/orders", body)).Content.ReadFromJsonAsync<JsonElement>();
        var session = first.GetProperty("tableSessionId").GetGuid();
        using var secondClient = await CashierAsync("round-two");
        var second = await (await secondClient.PostAsJsonAsync("/api/v1/orders", body)).Content.ReadFromJsonAsync<JsonElement>();
        second.GetProperty("tableSessionId").GetGuid().Should().Be(session);
        using var guest = _factory.CreateClient();
        var scan = await (await guest.PostAsJsonAsync("/api/v1/orders/qr/scan", new { qrToken = qr })).Content.ReadFromJsonAsync<JsonElement>();
        scan.GetProperty("sessionId").GetGuid().Should().Be(session);
        var bill = await firstClient.GetFromJsonAsync<JsonElement>($"/api/v1/orders/qr/sessions/{session}/bill");
        bill.GetProperty("orders").GetArrayLength().Should().Be(2);
        bill.GetProperty("due").GetDecimal().Should().Be(100_000);
        _factory.Clock.Now = _factory.Clock.Now.AddHours(7);
        using (var scope = _factory.Services.CreateScope())
        {
            var closed = await TableSessionExpiryWorker.CloseIdleAsync(scope.ServiceProvider.GetRequiredService<OrderingDbContext>(),
                _factory.Clock.Now.UtcDateTime, TimeSpan.FromHours(6), default);
            closed.Should().Be(0); // Idle time never settles an unpaid bill.
        }
        var filtered = await firstClient.GetFromJsonAsync<JsonElement>($"/api/v1/orders?tableId={table.TableId}&type=DineIn&includePaid=true");
        filtered.GetArrayLength().Should().Be(2);
        var takeaway = await firstClient.GetFromJsonAsync<JsonElement>("/api/v1/orders?type=Takeaway&includePaid=true");
        takeaway.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Delivered_unpaid_rounds_keep_delivery_metadata_in_list_detail_and_session_bill()
    {
        var qr = _tables.Add(_tenant, _branch, "S02");
        var table = (await _tables.LookupAsync(qr, default))!;
        using var client = await CashierAsync("delivered-round");
        var created = await (await client.PostAsJsonAsync("/api/v1/orders", new {
            branchId = _branch, type = "DineIn", tableId = table.TableId, tableNumber = table.TableCode,
            tableQrToken = qr, items = new[] { new { productId = _product, quantity = 1 } }
        })).Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetGuid();
        var session = created.GetProperty("tableSessionId").GetGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            var order = (await db.Orders.FindAsync(id))!;
            order.MarkAsReady(_factory.Clock.Now.UtcDateTime);
            await db.SaveChangesAsync();
        }
        (await client.PostAsJsonAsync($"/api/v1/orders/{id}/deliver", new { })).EnsureSuccessStatusCode();
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/v1/orders/{id}");
        var list = await client.GetFromJsonAsync<JsonElement>("/api/v1/orders?includePaid=true");
        var bill = await client.GetFromJsonAsync<JsonElement>($"/api/v1/orders/qr/sessions/{session}/bill");
        var round = bill.GetProperty("orders")[0];
        foreach (var snapshot in new[] { detail, list[0], round })
        {
            snapshot.GetProperty("deliveredAtUtc").GetDateTime().Should().Be(_factory.Clock.Now.UtcDateTime);
            snapshot.GetProperty("status").GetString().Should().Be("Ready");
            snapshot.GetProperty("paymentStatus").GetString().Should().Be("Unpaid");
        }
        round.GetProperty("cashierId").GetGuid().Should().Be(_cashier);
        round.GetProperty("readyAtUtc").GetDateTime().Should().Be(_factory.Clock.Now.UtcDateTime);
        bill.GetProperty("due").GetDecimal().Should().Be(50_000);
    }

    [Fact]
    public async Task A_table_reference_from_another_tenant_is_rejected()
    {
        var qr = _tables.Add(Guid.NewGuid(), _branch, "S02");
        var table = (await _tables.LookupAsync(qr, default))!;
        using var client = await CashierAsync("wrong-tenant");
        var response = await client.PostAsJsonAsync("/api/v1/orders", new { branchId = _branch, type = "DineIn",
            tableId = table.TableId, tableNumber = table.TableCode, tableQrToken = qr,
            items = new[] { new { productId = _product, quantity = 1 } } });
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Barista_may_read_capabilities_but_cannot_create_counter_orders()
    {
        using var barista = _factory.As(AicosRoles.Barista, _branch);
        (await barista.GetAsync("/api/v1/orders/staff/capabilities")).EnsureSuccessStatusCode();
        (await barista.PostAsJsonAsync("/api/v1/orders", Body())).StatusCode.Should().Be(System.Net.HttpStatusCode.Forbidden);
        (await barista.PostAsJsonAsync($"/api/v1/orders/qr/sessions/{Guid.NewGuid()}/close", new { })).StatusCode.Should().Be(System.Net.HttpStatusCode.Forbidden);
        using var anonymous = _factory.CreateClient();
        (await anonymous.GetAsync("/api/v1/orders/staff/capabilities")).StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);
    }
}
