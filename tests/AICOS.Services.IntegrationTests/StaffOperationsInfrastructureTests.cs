using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;
using AICOS.KitchenOperations.Api;
using AICOS.KitchenOperations.Api.Persistence;
using AICOS.KitchenOperations.Api.Services;
using AICOS.Ordering.Api;
using AICOS.Ordering.Api.Domain;
using Order = AICOS.Ordering.Api.Domain.Entities.Order;
using AICOS.Ordering.Api.Persistence;
using AICOS.Ordering.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using StackExchange.Redis;

namespace AICOS.Services.IntegrationTests;

[CollectionDefinition("Staff operations infrastructure", DisableParallelization = true)]
public sealed class StaffOperationsInfrastructureCollection { }

/// <summary>HTTP + real Npgsql + production Redis Pub/Sub. Identity/Menu/Table clients and JWT issuance are test doubles.</summary>
[Collection("Staff operations infrastructure")]
public sealed class StaffOperationsInfrastructureTests : IAsyncLifetime
{
    private ServiceFactory<OrderingService, OrderingDbContext> _orders = null!;
    private ServiceFactory<KitchenOperationsService, KitchenOperationsDbContext> _kitchen = null!;
    private IConnectionMultiplexer _redis = null!;
    private readonly FakeMenu _menu = new();
    private readonly FakeTableQr _tables = new();
    private readonly RecordingNotifier _notifications = new();
    private readonly Guid _branch = Guid.NewGuid(), _cashierId = Guid.NewGuid();
    private readonly Guid _tenant = ServiceFactory<OrderingService, OrderingDbContext>.DefaultTenantId;
    private Guid _product;
    private HttpClient _cashier = null!, _barista = null!;
    private string _orderingConnection = "", _kitchenConnection = "", _redisConnection = "";
    private readonly AdmissionCommitGate _admissionGate = new();

    private sealed class AdmissionCommitGate : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            if (Enabled)
            {
                Enabled = false;
                Entered.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }
            return result;
        }
    }

    public async Task InitializeAsync()
    {
        _orderingConnection = OperationalTestTarget.Required("AICOS_OPERATIONS_ORDERING_CONNECTION");
        _kitchenConnection = OperationalTestTarget.Required("AICOS_OPERATIONS_KITCHEN_CONNECTION");
        _redisConnection = OperationalTestTarget.Required("AICOS_OPERATIONS_REDIS");
        await OperationalTestTarget.VerifyAsync(_orderingConnection);
        await OperationalTestTarget.VerifyAsync(_kitchenConnection);
        await OperationalTestTarget.VerifyRedisAsync();
        _redis = await ConnectionMultiplexer.ConnectAsync(_redisConnection);
        _orders = new(services =>
        {
            services.RemoveAll<IMenuCatalogClient>(); services.AddSingleton<IMenuCatalogClient>(_menu);
            services.RemoveAll<ITableQrClient>(); services.AddSingleton<ITableQrClient>(_tables);
            foreach (var job in services.Where(s => s.ImplementationType?.Name is "PickupOrderExpiryWorker" or "TableSessionExpiryWorker").ToArray())
                services.Remove(job);
        }, _orderingConnection, operationalRedis: _redisConnection);
        _orders.Branches.Add(_tenant, id: _branch);
        _product = _menu.Add(_branch, "Operations test coffee", 50000);
        // Explicitly approved ephemeral schema initialization; never Migrate, EnsureDeleted, or a deployment database.
        using (var scope = _orders.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<OrderingDbContext>().Database.EnsureCreatedAsync();
        await StartKitchenAsync();
        _cashier = _orders.As(AicosRoles.Cashier, _branch, _cashierId);
        await WaitForSubscriptionsAsync();
    }

    private async Task StartKitchenAsync()
    {
        _kitchen = new(services =>
        {
            services.RemoveAll<IKitchenNotifier>(); services.AddSingleton<IKitchenNotifier>(_notifications);
        }, _kitchenConnection, configureDatabase: options => options.AddInterceptors(_admissionGate), operationalRedis: _redisConnection);
        _kitchen.Branches.Add(_tenant, id: _branch);
        using (var scope = _kitchen.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<KitchenOperationsDbContext>().Database.EnsureCreatedAsync();
        _barista = _kitchen.As(AicosRoles.Barista, _branch);
    }

    public async Task DisposeAsync()
    {
        _cashier?.Dispose(); _barista?.Dispose();
        if (_kitchen is not null) await _kitchen.DisposeAsync();
        if (_orders is not null) await _orders.DisposeAsync();
        if (_redis is not null) await _redis.DisposeAsync();
    }

    private object Request(Guid? expectedSession = null, string? qr = null, Guid? table = null, string? tableNumber = null) => new
    {
        branchId = _branch,
        type = "DineIn",
        expectedSessionId = expectedSession,
        tableQrToken = qr,
        tableId = table,
        tableNumber,
        items = new[] { new { productId = _product, quantity = 1 } },
    };

    private async Task<HttpResponseMessage> CreateAsync(string key, object? body = null, HttpClient? client = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders") { Content = JsonContent.Create(body ?? Request()) };
        request.Headers.Add("Idempotency-Key", key);
        return await (client ?? _cashier).SendAsync(request);
    }

    private async Task<Guid> NewOrderAsync()
    {
        var response = await CreateAsync(Guid.NewGuid().ToString("N"));
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Body()).Id();
    }

    private async Task<AICOS.KitchenOperations.Api.Domain.Entities.KitchenTicket?> TicketAsync(Guid order)
    {
        using var scope = _kitchen.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<KitchenOperationsDbContext>().Tickets.AsNoTracking()
            .SingleOrDefaultAsync(t => t.OrderId == order);
    }

    private async Task<Order> OrderAsync(Guid id)
    {
        using var scope = _orders.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<OrderingDbContext>().Orders.AsNoTracking().SingleAsync(o => o.Id == id);
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition, string reason)
    {
        var until = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < until)
        {
            if (await condition()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException(reason);
    }

    private static async Task<T> WithLockContentionAsync<T>(string connection, string key, Func<Task<T>> requests)
    {
        await using var blocker = new NpgsqlConnection(connection);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (var acquire = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))", blocker, transaction))
        {
            acquire.Parameters.AddWithValue("key", key);
            await acquire.ExecuteNonQueryAsync();
        }
        var pending = requests();
        try
        {
            await EventuallyAsync(async () =>
            {
                await using var locks = new NpgsqlCommand("SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND NOT granted AND database = (SELECT oid FROM pg_database WHERE datname = current_database())", blocker, transaction);
                return (long)(await locks.ExecuteScalarAsync())! >= 2;
            }, "Two real PostgreSQL writers did not wait on the operation lock.");
        }
        finally { await transaction.CommitAsync(); }
        return await pending;
    }

    private async Task WaitForSubscriptionsAsync() => await EventuallyAsync(async () =>
    {
        var server = _redis.GetServer(_redis.GetEndPoints()[0]);
        return await server.SubscriptionSubscriberCountAsync(RedisChannel.Literal("aicos:events:OrderCreatedIntegrationEvent")) > 0
            && await server.SubscriptionSubscriberCountAsync(RedisChannel.Literal("aicos:events:KitchenOrderReadyIntegrationEvent")) > 0;
    }, "Real Redis subscribers did not become ready.");

    private async Task<Guid> TicketIdAsync(Guid order)
    {
        await EventuallyAsync(async () => await TicketAsync(order) is not null, "OrderCreated did not reach Kitchen via Redis.");
        return (await TicketAsync(order))!.Id;
    }

    private async Task ReadyAsync(Guid order)
    {
        var id = await TicketIdAsync(order);
        await _barista.Put($"/api/v1/kitchen/tickets/{id}/status", new
        { status = "ReadyForPickup", expectedStatus = "Pending", expectedRemakeCount = 0 }).ShouldBe(200);
        await EventuallyAsync(async () => (await OrderAsync(order)).Status.ToString() == "Ready", "Ready did not reach Ordering via Redis.");
    }

    [OperationalInfrastructureFact]
    public async Task Payment_during_admission_commit_waits_and_makes_takeaway_visible()
    {
        _admissionGate.Enabled = true;
        var response = await CreateAsync(Guid.NewGuid().ToString("N"), new
        {
            branchId = _branch,
            type = "Takeaway",
            items = new[] { new { productId = _product, quantity = 2 } },
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var order = (await response.Body()).Id();
        Task payment = Task.CompletedTask;
        try
        {
            await _admissionGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            // Admission has saved its row but has not committed. Invoke the real handler directly
            // so the test can distinguish early return from a PostgreSQL lock wait without sleeps.
            payment = _kitchen.HandleAsync(new PaymentSucceededEvent(_tenant, _branch, Guid.NewGuid(), order,
                100000, "Cash", DateTime.UtcNow));
            await EventuallyAsync(async () =>
            {
                if (payment.IsCompleted) return true;
                await using var connection = new NpgsqlConnection(_kitchenConnection);
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND NOT granted AND database = (SELECT oid FROM pg_database WHERE datname = current_database())", connection);
                return (long)(await command.ExecuteScalarAsync())! > 0;
            }, "Payment neither completed nor waited on the admission lock.");
        }
        finally { _admissionGate.Release.TrySetResult(); }
        await payment;
        await TicketIdAsync(order);
        (await TicketAsync(order))!.IsPaid.Should().BeTrue("payment must not be discarded while admission is committing");
        var queue = await _barista.GetFromJsonAsync<JsonElement>("/api/v1/kitchen/queue");
        queue.EnumerateArray().Should().Contain(t => t.GetProperty("orderId").GetGuid() == order);
    }

    [OperationalInfrastructureFact]
    public async Task Paid_takeaway_reaches_the_queue_through_real_Redis()
    {
        var response = await CreateAsync(Guid.NewGuid().ToString("N"), new
        {
            branchId = _branch,
            type = "Takeaway",
            items = new[] { new { productId = _product, quantity = 2 } },
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var order = (await response.Body()).Id();
        await TicketIdAsync(order);
        var payment = new PaymentSucceededEvent(_tenant, _branch, Guid.NewGuid(), order,
            100000, "Cash", DateTime.UtcNow);
        await _orders.Services.GetRequiredService<AICOS.BuildingBlocks.Messaging.IEventBus>().PublishAsync(payment);
        await EventuallyAsync(async () => (await TicketAsync(order))!.IsPaid, "Kitchen did not persist payment.");
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => _kitchen.HandleAsync(payment)));
        var queue = await _barista.GetFromJsonAsync<JsonElement>("/api/v1/kitchen/queue");
        queue.EnumerateArray().Should().Contain(t => t.GetProperty("orderId").GetGuid() == order);
        using var scope = _kitchen.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KitchenOperationsDbContext>();
        (await db.Tickets.CountAsync(t => t.OrderId == order)).Should().Be(1);
        var ticket = (await TicketAsync(order))!;
        await EventuallyAsync(async () => await db.CupStickers.CountAsync(s => s.TicketId == ticket.Id) == 2,
            "Payment must generate exactly one sticker set after its commit.");
    }

    [OperationalInfrastructureFact]
    public async Task Two_baristas_and_parallel_duplicate_updates_persist_one_transition()
    {
        var order = await NewOrderAsync(); var ticket = await TicketIdAsync(order);
        using var second = _kitchen.As(AicosRoles.Barista, _branch);
        var responses = await WithLockContentionAsync(_kitchenConnection, $"aicos:kitchen-operation:{_tenant}:{order}",
            () => Task.WhenAll(Enumerable.Range(0, 8).Select(i => (i % 2 == 0 ? _barista : second)
                .Put($"/api/v1/kitchen/tickets/{ticket}/status", new { status = "Brewing", expectedStatus = "Pending", expectedRemakeCount = 0 }))));
        foreach (var response in responses) response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await TicketAsync(order))!.Status.ToString().Should().Be("Brewing");
        _notifications.Statuses.Count(s => s.Status == "Brewing").Should().Be(1);
        await EventuallyAsync(async () => (await OrderAsync(order)).Status.ToString() == "Preparing", "Started event missing.");
    }

    [OperationalInfrastructureFact]
    public async Task Concurrent_legacy_sticker_generation_persists_one_set_and_preview_is_read_only()
    {
        var order = await NewOrderAsync(); var ticket = await TicketIdAsync(order);
        using (var scope = _kitchen.Services.CreateScope())
        {
            // Simulate an old ticket without stickers only in the guarded disposable database.
            var db = scope.ServiceProvider.GetRequiredService<KitchenOperationsDbContext>();
            await db.CupStickers.Where(s => s.TicketId == ticket).ExecuteDeleteAsync();
        }
        var path = $"/api/v1/kitchen/tickets/{ticket}/stickers";
        using var second = _kitchen.As(AicosRoles.Barista, _branch);
        var results = await WithLockContentionAsync(_kitchenConnection, $"aicos:kitchen-operation:{_tenant}:{order}",
            () => Task.WhenAll(_barista.GetAsync(path), second.GetAsync(path)));
        foreach (var response in results) response.StatusCode.Should().Be(HttpStatusCode.OK);
        var first = await results[0].Body(); var replay = await results[1].Body();
        first.GetProperty("stickers")[0].GetProperty("stickerId").GetGuid()
            .Should().Be(replay.GetProperty("stickers")[0].GetProperty("stickerId").GetGuid());
        using var verification = _kitchen.Services.CreateScope();
        var database = verification.ServiceProvider.GetRequiredService<KitchenOperationsDbContext>();
        var before = await database.CupStickers.AsNoTracking().Where(s => s.TicketId == ticket).SingleAsync();
        await _barista.GetAsync($"/api/v1/kitchen/tickets/{ticket}/sticker-preview").ShouldBe(200);
        var after = await database.CupStickers.AsNoTracking().Where(s => s.TicketId == ticket).SingleAsync();
        after.Id.Should().Be(before.Id); after.PrintedAtUtc.Should().Be(before.PrintedAtUtc);
    }

    [OperationalInfrastructureFact]
    public async Task Transfer_event_and_preparation_share_the_same_postgresql_order_lock()
    {
        var order = await NewOrderAsync(); var ticket = await TicketIdAsync(order);
        var transfer = new TableTransferredIntegrationEvent(_tenant, _branch, Guid.NewGuid(), Guid.NewGuid(), "S09", [order]);
        await WithLockContentionAsync(_kitchenConnection, $"aicos:kitchen-operation:{_tenant}:{order}", async () =>
        {
            var transferring = _kitchen.HandleAsync(transfer);
            var preparing = _barista.Put($"/api/v1/kitchen/tickets/{ticket}/status", new
            { status = "Brewing", expectedStatus = "Pending", expectedRemakeCount = 0 });
            await Task.WhenAll(transferring, preparing);
            (await preparing).StatusCode.Should().Be(HttpStatusCode.OK);
            return true;
        });
        var saved = (await TicketAsync(order))!;
        saved.Status.ToString().Should().Be("Brewing"); saved.TableNumber.Should().Be("S09");
    }

    [OperationalInfrastructureFact]
    public async Task Two_staff_deliver_once_and_unpaid_ticket_completes_through_real_redis()
    {
        var order = await NewOrderAsync(); await ReadyAsync(order);
        using var second = _orders.As(AicosRoles.Cashier, _branch, Guid.NewGuid());
        var responses = await WithLockContentionAsync(_orderingConnection, $"aicos:delivery:{_tenant}:{order}",
            () => Task.WhenAll(_cashier.Post($"/api/v1/orders/{order}/deliver", new { }), second.Post($"/api/v1/orders/{order}/deliver", new { })));
        foreach (var response in responses) response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var saved = await OrderAsync(order);
        saved.DeliveredAtUtc.Should().NotBeNull(); saved.DeliveredBy.Should().NotBeNull();
        saved.PaymentStatus.ToString().Should().Be("Unpaid"); saved.Status.ToString().Should().Be("Ready");
        var originalActor = saved.DeliveredBy; var originalTime = saved.DeliveredAtUtc;
        await second.Post($"/api/v1/orders/{order}/deliver", new { }).ShouldBe(200);
        var replay = await OrderAsync(order);
        replay.DeliveredBy.Should().Be(originalActor); replay.DeliveredAtUtc.Should().Be(originalTime);
        await EventuallyAsync(async () => (await TicketAsync(order))!.Status.ToString() == "Completed", "Delivered event missing.");
        (await TicketAsync(order))!.IsPaid.Should().BeFalse();
        _notifications.Statuses.Count(s => s.Status == "Completed").Should().Be(1);
    }

    [OperationalInfrastructureFact]
    public async Task Parallel_create_requests_with_one_key_persist_one_order_and_ticket()
    {
        var key = Guid.NewGuid().ToString("N");
        var responses = await WithLockContentionAsync(_orderingConnection, $"aicos:counter:{_tenant}:{_cashierId}:{key}",
            () => Task.WhenAll(Enumerable.Range(0, 10).Select(_ => CreateAsync(key))));
        responses.Count(r => r.StatusCode == HttpStatusCode.Created).Should().Be(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(9);
        var ids = new List<Guid>(); foreach (var response in responses) ids.Add((await response.Body()).Id());
        ids.Distinct().Should().ContainSingle(); await TicketIdAsync(ids[0]);
        using var scope = _orders.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<OrderingDbContext>().Orders.CountAsync(o => o.BranchId == _branch)).Should().Be(1);
    }

    [OperationalInfrastructureFact]
    public async Task Stale_status_and_remake_cycle_return_http_409()
    {
        var order = await NewOrderAsync(); var ticket = await TicketIdAsync(order);
        var path = $"/api/v1/kitchen/tickets/{ticket}/status";
        await _barista.Put(path, new { status = "Brewing", expectedStatus = "Pending", expectedRemakeCount = 0 }).ShouldBe(200);
        await _barista.Put(path, new { status = "ReadyForPickup", expectedStatus = "Pending", expectedRemakeCount = 0 }).ShouldBe(409, "ticket.stale");
        await _barista.Put(path, new { status = "ReadyForPickup", expectedStatus = "Brewing", expectedRemakeCount = 0 }).ShouldBe(200);
        await _barista.Post($"/api/v1/kitchen/tickets/{ticket}/remake", new { reason = "Test remake" }).ShouldBe(200);
        await _barista.Put(path, new { status = "ReadyForPickup", expectedStatus = "Brewing", expectedRemakeCount = 0 }).ShouldBe(409, "ticket.stale");
    }

    [OperationalInfrastructureFact]
    public async Task Add_more_rejects_the_old_session_after_a_new_sitting_starts()
    {
        var qr = _tables.Add(_tenant, _branch, "OPS-SESSION"); var table = (await _tables.LookupAsync(qr, default))!;
        var firstResponse = await CreateAsync("first", Request(qr: qr, table: table.TableId, tableNumber: table.TableCode));
        firstResponse.StatusCode.Should().Be(HttpStatusCode.Created, await firstResponse.Content.ReadAsStringAsync());
        var first = await firstResponse.Body();
        var session = first.GetProperty("tableSessionId").GetGuid();
        using (var scope = _orders.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            (await db.TableSessions.FindAsync(session))!.Close(TableSessionCloseReason.Staff, DateTime.UtcNow);
            await db.SaveChangesAsync(); // Fixture boundary, not an assertion about public close/payment behavior.
        }
        var nextResponse = await CreateAsync("next", Request(qr: qr, table: table.TableId, tableNumber: table.TableCode));
        nextResponse.StatusCode.Should().Be(HttpStatusCode.Created, await nextResponse.Content.ReadAsStringAsync());
        var next = await nextResponse.Body();
        next.GetProperty("tableSessionId").GetGuid().Should().NotBe(session);
        await CreateAsync("stale", Request(session, qr, table.TableId, table.TableCode)).ShouldBe(409, "qr.session_changed");
        using var check = _orders.Services.CreateScope();
        (await check.ServiceProvider.GetRequiredService<OrderingDbContext>().Orders.CountAsync(o => o.BranchId == _branch)).Should().Be(2);
    }

    [OperationalInfrastructureFact]
    public async Task Retrying_after_a_lost_http_response_reuses_committed_create_and_delivery()
    {
        using var lostCreate = _orders.CreateDefaultClient(new LostSuccessfulResponse());
        lostCreate.DefaultRequestHeaders.Authorization = _cashier.DefaultRequestHeaders.Authorization;
        var key = Guid.NewGuid().ToString("N");
        await Assert.ThrowsAsync<TaskCanceledException>(() => CreateAsync(key, client: lostCreate));
        var response = await CreateAsync(key); response.StatusCode.Should().Be(HttpStatusCode.OK);
        var order = (await response.Body()).Id(); await ReadyAsync(order);
        using var lostDelivery = _orders.CreateDefaultClient(new LostSuccessfulResponse());
        lostDelivery.DefaultRequestHeaders.Authorization = _cashier.DefaultRequestHeaders.Authorization;
        await Assert.ThrowsAsync<TaskCanceledException>(() => lostDelivery.Post($"/api/v1/orders/{order}/deliver", new { }));
        var original = await OrderAsync(order);
        await _cashier.Post($"/api/v1/orders/{order}/deliver", new { }).ShouldBe(200);
        var replay = await OrderAsync(order);
        replay.DeliveredBy.Should().Be(original.DeliveredBy); replay.DeliveredAtUtc.Should().Be(original.DeliveredAtUtc);
        await EventuallyAsync(async () => (await TicketAsync(order))!.Status.ToString() == "Completed", "Retry did not reach Kitchen.");
    }

    [OperationalInfrastructureFact]
    public async Task Redis_restart_reconnects_but_does_not_recover_an_event_published_while_kitchen_is_offline()
    {
        var existing = await NewOrderAsync(); await TicketIdAsync(existing);
        _barista.Dispose(); await _kitchen.DisposeAsync(); _kitchen = null!;
        await EventuallyAsync(async () => await _redis.GetServer(_redis.GetEndPoints()[0])
            .SubscriptionSubscriberCountAsync(RedisChannel.Literal("aicos:events:OrderCreatedIntegrationEvent")) == 0, "Kitchen subscriber did not stop.");
        await OperationalTestTarget.RestartRedisAsync();
        await EventuallyAsync(async () =>
        {
            try { await _redis.GetDatabase().PingAsync(); return true; } catch (RedisException) { return false; }
        }, "Disposable Redis did not restart.");
        var missed = await NewOrderAsync();
        await StartKitchenAsync(); await WaitForSubscriptionsAsync();
        (await TicketAsync(existing)).Should().NotBeNull("PostgreSQL survives a Redis restart");
        await Task.Delay(2000);
        (await TicketAsync(missed)).Should().BeNull("current Pub/Sub has no durable replay; this characterizes the recovery GAP, not successful recovery");
        (await OrderAsync(missed)).PaymentStatus.ToString().Should().Be("Unpaid");
        await TicketIdAsync(await NewOrderAsync()); // New live events work after the reconnect.
    }

    private sealed class LostSuccessfulResponse : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode(); response.Dispose();
            throw new TaskCanceledException("Test-only lost response after the handler committed.");
        }
    }
}
