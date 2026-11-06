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

    [Fact]
    public async Task Expected_session_cannot_silently_open_a_new_sitting_and_legacy_requests_still_work()
    {
        var qr = _tables.Add(_tenant, _branch, "SESSION-GUARD");
        var table = (await _tables.LookupAsync(qr, default))!;
        object Request(Guid? expected = null) => new { branchId = _branch, type = "DineIn",
            tableId = table.TableId, tableNumber = table.TableCode, tableQrToken = qr, expectedSessionId = expected,
            items = new[] { new { productId = _product, quantity = 1 } } };
        using var firstClient = await CashierAsync("expected-first");
        var original = await firstClient.PostOk("/api/v1/orders", Request());
        var sessionId = original.GetProperty("tableSessionId").GetGuid();
        using var more = await CashierAsync("expected-more");
        var second = await more.PostOk("/api/v1/orders", Request(sessionId));
        second.GetProperty("tableSessionId").GetGuid().Should().Be(sessionId);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            (await db.TableSessions.FindAsync(sessionId))!.Close(AICOS.Ordering.Api.Domain.TableSessionCloseReason.Staff, DateTime.UtcNow);
            await db.SaveChangesAsync();
        }
        using var stale = await CashierAsync("expected-stale");
        await stale.Post("/api/v1/orders", Request(sessionId)).ShouldBe(409, "qr.session_changed");
        // Replay reads the previously committed order, not a new order on a closed session.
        var replay = await more.PostOk("/api/v1/orders", Request(sessionId));
        replay.GetProperty("id").GetGuid().Should().Be(second.GetProperty("id").GetGuid());
        using var scopeAfter = _factory.Services.CreateScope();
        scopeAfter.ServiceProvider.GetRequiredService<OrderingDbContext>().TableSessions.Count().Should().Be(1);
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
        var body = new
        {
            branchId = _branch,
            type = "DineIn",
            tableId = table.TableId,
            tableNumber = table.TableCode,
            tableQrToken = qr,
            items = new[] { new { productId = _product, quantity = 1 } }
        };
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
        var created = await (await client.PostAsJsonAsync("/api/v1/orders", new
        {
            branchId = _branch,
            type = "DineIn",
            tableId = table.TableId,
            tableNumber = table.TableCode,
            tableQrToken = qr,
            items = new[] { new { productId = _product, quantity = 1 } }
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
        var response = await client.PostAsJsonAsync("/api/v1/orders", new
        {
            branchId = _branch,
            type = "DineIn",
            tableId = table.TableId,
            tableNumber = table.TableCode,
            tableQrToken = qr,
            items = new[] { new { productId = _product, quantity = 1 } }
        });
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Delivered_then_cancelled_unpaid_round_closes_without_recording_payment()
    {
        var qr = _tables.Add(_tenant, _branch, "S10");
        var table = (await _tables.LookupAsync(qr, default))!;
        using var client = await CashierAsync("cancel-delivered-round");
        var body = new
        {
            branchId = _branch,
            type = "DineIn",
            tableId = table.TableId,
            tableNumber = table.TableCode,
            tableQrToken = qr,
            items = new[] { new { productId = _product, quantity = 1 } }
        };
        var first = await client.PostAsJsonAsync("/api/v1/orders", body);
        first.EnsureSuccessStatusCode();
        var created = await first.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetGuid();
        var session = created.GetProperty("tableSessionId").GetGuid();
        (await client.PostAsJsonAsync("/api/v1/orders", body)).EnsureSuccessStatusCode();
        _factory.Events.Published.OfType<AICOS.Contracts.Events.OrderCreatedIntegrationEvent>()
            .Should().ContainSingle(); // Replaying a committed request must not send a second ticket event.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            var order = (await db.Orders.FindAsync(id))!;
            order.MarkAsReady(_factory.Clock.Now.UtcDateTime);
            await db.SaveChangesAsync();
        }
        var delivered = await (await client.PostAsJsonAsync($"/api/v1/orders/{id}/deliver", new { })).Content.ReadFromJsonAsync<JsonElement>();
        delivered.GetProperty("paymentStatus").GetString().Should().Be("Unpaid");
        delivered.GetProperty("deliveredAtUtc").ValueKind.Should().Be(JsonValueKind.String);
        (await client.PostAsJsonAsync($"/api/v1/orders/qr/sessions/{session}/close", new { }))
            .StatusCode.Should().Be(System.Net.HttpStatusCode.Conflict);
        (await client.PostAsJsonAsync($"/api/v1/orders/{id}/cancel", new { reason = "Marked local test cleanup" }))
            .EnsureSuccessStatusCode();
        var bill = await client.GetFromJsonAsync<JsonElement>($"/api/v1/orders/qr/sessions/{session}/bill");
        bill.GetProperty("due").GetDecimal().Should().Be(0);
        (await client.PostAsJsonAsync($"/api/v1/orders/qr/sessions/{session}/close", new { }))
            .StatusCode.Should().Be(System.Net.HttpStatusCode.NoContent);
        var sessions = await client.GetFromJsonAsync<JsonElement>($"/api/v1/orders/qr/sessions?branchId={_branch}");
        sessions.GetArrayLength().Should().Be(0);
        var final = await client.GetFromJsonAsync<JsonElement>($"/api/v1/orders/{id}");
        final.GetProperty("status").GetString().Should().Be("Cancelled");
        final.GetProperty("paymentStatus").GetString().Should().Be("Unpaid");
    }

    [Fact]
    public async Task Transfer_moves_all_unpaid_rounds_without_new_orders_or_price_changes()
    {
        var sourceQr = _tables.Add(_tenant, _branch, "S01");
        var targetQr = _tables.Add(_tenant, _branch, "S02");
        var source = (await _tables.LookupAsync(sourceQr, default))!;
        var target = (await _tables.LookupAsync(targetQr, default))!;
        var body = new { branchId = _branch, type = "DineIn", tableId = source.TableId,
            tableNumber = source.TableCode, tableQrToken = sourceQr, items = new[] { new { productId = _product, quantity = 1 } } };
        using var first = await CashierAsync("transfer-round-1");
        using var second = await CashierAsync("transfer-round-2");
        var one = await (await first.PostAsJsonAsync("/api/v1/orders", body)).Content.ReadFromJsonAsync<JsonElement>();
        var two = await (await second.PostAsJsonAsync("/api/v1/orders", body)).Content.ReadFromJsonAsync<JsonElement>();
        var session = one.GetProperty("tableSessionId").GetGuid();
        var move = new { expectedTableId = source.TableId, targetQrToken = targetQr };
        var url = $"/api/v1/orders/qr/sessions/{session}/transfer";
        (await first.PostAsJsonAsync(url, move)).EnsureSuccessStatusCode();
        (await first.PostAsJsonAsync(url, move)).EnsureSuccessStatusCode(); // lost-response replay does not create another session/order
        foreach (var created in new[] { one, two })
        {
            var order = await first.GetFromJsonAsync<JsonElement>($"/api/v1/orders/{created.GetProperty("id").GetGuid()}");
            order.GetProperty("tableId").GetGuid().Should().Be(target.TableId);
            order.GetProperty("tableSessionId").GetGuid().Should().Be(session);
            order.GetProperty("finalAmount").GetDecimal().Should().Be(50000);
        }
        var sessions = await first.GetFromJsonAsync<JsonElement>($"/api/v1/orders/qr/sessions?branchId={_branch}");
        sessions.GetArrayLength().Should().Be(1);
        sessions[0].GetProperty("tableCode").GetString().Should().Be("S02");
        var creators = await first.GetFromJsonAsync<JsonElement>($"/api/v1/orders/staff/creators?branchId={_branch}&ids={one.GetProperty("id").GetGuid()},{two.GetProperty("id").GetGuid()}");
        creators.GetArrayLength().Should().Be(2);
        creators[0].GetProperty("cashierId").GetGuid().Should().Be(_cashier);
        _factory.Events.Published.OfType<AICOS.Contracts.Events.TableTransferredIntegrationEvent>().Last().OrderIds.Should().HaveCount(2);
    }

    [Fact]
    public async Task Transfer_rejects_occupied_paid_stale_cross_branch_and_barista_requests()
    {
        var sourceQr = _tables.Add(_tenant, _branch, "S01");
        var targetQr = _tables.Add(_tenant, _branch, "S02");
        var emptyQr = _tables.Add(_tenant, _branch, "S03");
        var source = (await _tables.LookupAsync(sourceQr, default))!;
        var target = (await _tables.LookupAsync(targetQr, default))!;
        using var client = await CashierAsync("guard-source");
        var original = await (await client.PostAsJsonAsync("/api/v1/orders", new { branchId = _branch, type = "DineIn", tableId = source.TableId,
            tableNumber = source.TableCode, tableQrToken = sourceQr, items = new[] { new { productId = _product, quantity = 1 } } })).Content.ReadFromJsonAsync<JsonElement>();
        var session = original.GetProperty("tableSessionId").GetGuid();
        using var other = await CashierAsync("guard-target");
        (await other.PostAsJsonAsync("/api/v1/orders", new { branchId = _branch, type = "DineIn", tableId = target.TableId,
            tableNumber = target.TableCode, tableQrToken = targetQr, items = new[] { new { productId = _product, quantity = 1 } } })).EnsureSuccessStatusCode();
        var url = $"/api/v1/orders/qr/sessions/{session}/transfer";
        (await client.PostAsJsonAsync(url, new { expectedTableId = source.TableId, targetQrToken = targetQr })).StatusCode.Should().Be(System.Net.HttpStatusCode.Conflict);
        (await client.PostAsJsonAsync(url, new { expectedTableId = Guid.NewGuid(), targetQrToken = emptyQr })).StatusCode.Should().Be(System.Net.HttpStatusCode.Conflict);
        using var barista = _factory.As(AicosRoles.Barista, _branch);
        (await barista.PostAsJsonAsync(url, new { expectedTableId = source.TableId, targetQrToken = emptyQr })).StatusCode.Should().Be(System.Net.HttpStatusCode.Forbidden);
        var foreignBranch = _factory.Branches.Add(_tenant);
        var foreignQr = _tables.Add(_tenant, foreignBranch, "F01");
        (await client.PostAsJsonAsync(url, new { expectedTableId = source.TableId, targetQrToken = foreignQr })).StatusCode.Should().Be(System.Net.HttpStatusCode.Forbidden);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            (await db.Orders.FindAsync(original.GetProperty("id").GetGuid()))!.MarkAsPaid(AICOS.Ordering.Api.Domain.Enums.PaymentMethod.Cash,
                "Cash", Guid.NewGuid(), DateTime.UtcNow);
            await db.SaveChangesAsync();
        }
        (await client.PostAsJsonAsync(url, new { expectedTableId = source.TableId, targetQrToken = emptyQr })).StatusCode.Should().Be(System.Net.HttpStatusCode.Conflict);
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
