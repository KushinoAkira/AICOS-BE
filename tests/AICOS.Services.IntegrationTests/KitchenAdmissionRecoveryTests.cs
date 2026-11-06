using System.Diagnostics;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;
using AICOS.Contracts.KitchenAdmission;
using AICOS.KitchenOperations.Api;
using AICOS.KitchenOperations.Api.Features.KitchenAdmission;
using AICOS.KitchenOperations.Api.Persistence;
using AICOS.KitchenOperations.Api.Services;
using AICOS.Ordering.Api;
using AICOS.Ordering.Api.Domain.Entities;
using AICOS.Ordering.Api.Domain.Enums;
using AICOS.Ordering.Api.Features.KitchenAdmission;
using AICOS.Ordering.Api.Persistence;
using AICOS.Ordering.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace AICOS.Services.IntegrationTests;

/// <summary>Real PostgreSQL migrations/transactions, production Redis transport, real machine auth and HTTP handlers.
/// Identity/Menu/Table are doubles. Crash probes are separate killed OS processes sharing only guarded test databases.</summary>
[Collection("Staff operations infrastructure")]
public sealed class KitchenAdmissionRecoveryTests : IAsyncLifetime
{
    private ServiceFactory<OrderingService, OrderingDbContext> _orders = null!;
    private ServiceFactory<KitchenOperationsService, KitchenOperationsDbContext>? _kitchen;
    private readonly FakeMenu _menu = new();
    private readonly FakeTableQr _tables = new();
    private readonly CaptureFailure _captureFailure = new();
    private readonly CommitFailure _commitFailure = new();
    private readonly Guid _tenant = ServiceFactory<OrderingService, OrderingDbContext>.DefaultTenantId, _branch = Guid.NewGuid(), _cashierId = Guid.NewGuid();
    private Guid _product;
    private string _ordering = "", _kitchenConnection = "", _redis = "", _key = "";
    private readonly Dictionary<string, string?> _prior = new();
    private RecoveryTransport _transport = null!;
    private bool _automatic;

    public async Task InitializeAsync()
    {
        _ordering = OperationalTestTarget.Required("AICOS_OPERATIONS_ORDERING_CONNECTION");
        _kitchenConnection = OperationalTestTarget.Required("AICOS_OPERATIONS_KITCHEN_CONNECTION");
        _redis = OperationalTestTarget.Required("AICOS_OPERATIONS_REDIS");
        await OperationalTestTarget.VerifyAsync(_ordering); await OperationalTestTarget.VerifyAsync(_kitchenConnection);
        await OperationalTestTarget.VerifyRedisAsync();
        _key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        foreach (var setting in new Dictionary<string, string> { ["KitchenAdmission__CaptureEnabled"] = "true", ["KitchenAdmission__ReceiverEnabled"] = "true", ["KitchenAdmission__MachineKey"] = _key })
        { _prior[setting.Key] = Environment.GetEnvironmentVariable(setting.Key); Environment.SetEnvironmentVariable(setting.Key, setting.Value); }
        _transport = new RecoveryTransport(() => _kitchen?.CreateClient(), _key);
        StartOrdering();
        using (var scope = _orders.Services.CreateScope()) await scope.ServiceProvider.GetRequiredService<OrderingDbContext>().Database.MigrateAsync();
        await StartKitchenAsync();
    }

    private void StartOrdering()
    {
        _orders = new(services =>
        {
            services.RemoveAll<IMenuCatalogClient>(); services.AddSingleton<IMenuCatalogClient>(_menu);
            services.RemoveAll<ITableQrClient>(); services.AddSingleton<ITableQrClient>(_tables);
            services.RemoveAll<IKitchenAdmissionTransport>(); services.AddSingleton<IKitchenAdmissionTransport>(_transport);
            if (_automatic) { services.RemoveAll<TimeProvider>(); services.AddSingleton(TimeProvider.System); }
            foreach (var job in services.Where(s => s.ImplementationType?.Name is "PickupOrderExpiryWorker" or "TableSessionExpiryWorker").ToArray()) services.Remove(job);
        }, _ordering, options => options.AddInterceptors(_captureFailure, _commitFailure), _redis);
        _orders.Branches.Add(_tenant, id: _branch);
        if (_product == Guid.Empty) _product = _menu.Add(_branch, "Recovery coffee", 50000);
    }

    private async Task StartKitchenAsync()
    {
        _kitchen = new(services => { services.RemoveAll<IKitchenNotifier>(); services.AddSingleton<IKitchenNotifier>(new RecordingNotifier()); }, _kitchenConnection, operationalRedis: _redis);
        _kitchen.Branches.Add(_tenant, id: _branch);
        using var scope = _kitchen.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<KitchenOperationsDbContext>().Database.MigrateAsync();
    }
    private async Task StopKitchenAsync() { if (_kitchen is not null) { await _kitchen.DisposeAsync(); _kitchen = null; } }
    public async Task DisposeAsync()
    {
        await StopKitchenAsync(); if (_orders is not null) await _orders.DisposeAsync();
        foreach (var prior in _prior) Environment.SetEnvironmentVariable(prior.Key, prior.Value);
    }

    private async Task<Guid> CreateAsync(string? key = null)
    {
        using var client = _orders.As(AicosRoles.Cashier, _branch, _cashierId);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
        {
            Content = JsonContent.Create(new
            { branchId = _branch, type = "DineIn", items = new[] { new { productId = _product, quantity = 1, note = "Less ice" } } })
        };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString("N"));
        var response = await client.SendAsync(request);
        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync());
        return (await response.Body()).Id();
    }
    private async Task<AdmissionOutbox> RowAsync(Guid order)
    {
        using var scope = _orders.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<OrderingDbContext>().AdmissionOutbox.AsNoTracking().SingleAsync(r => r.OrderId == order);
    }
    private async Task<AdmissionRequest> PayloadAsync(Guid order)
    {
        using var scope = _orders.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        var source = await db.Orders.AsNoTracking().Include(o => o.Items).ThenInclude(i => i.Options).SingleAsync(o => o.Id == order);
        return AdmissionProjection.Request(await db.AdmissionOutbox.SingleAsync(r => r.OrderId == order), source);
    }
    private async Task<(int Tickets, int Items, int Stickers, string? Status, string? Hold)> CountsAsync(Guid order)
    {
        await using var db = new KitchenOperationsDbContext(new DbContextOptionsBuilder<KitchenOperationsDbContext>().UseNpgsql(_kitchenConnection).UseSnakeCaseNamingConvention().Options);
        var rows = await db.Tickets.AsNoTracking().Include(t => t.Items).Include(t => t.Stickers).Where(t => t.OrderId == order).ToListAsync();
        return (rows.Count, rows.Sum(t => t.Items.Count), rows.Sum(t => t.Stickers.Count), rows.SingleOrDefault()?.Status.ToString(), rows.SingleOrDefault()?.RecoveryHoldReason);
    }
    private async Task<int> DispatchAsync()
    {
        _orders.Clock.Now = DateTimeOffset.UtcNow;
        using var scope = _orders.Services.CreateScope(); return await scope.ServiceProvider.GetRequiredService<KitchenAdmissionDispatcher>().DispatchOnceAsync();
    }
    private async Task ReconcileAsync()
    {
        _orders.Clock.Now = DateTimeOffset.UtcNow;
        using var scope = _orders.Services.CreateScope(); await scope.ServiceProvider.GetRequiredService<KitchenAdmissionReconciler>().ReconcileOnceAsync();
    }
    private async Task EnsureBackfilledAsync(Guid id)
    {
        for (var i = 0; i < 10; i++)
        {
            await ReconcileAsync(); using var scope = _orders.Services.CreateScope();
            if (await scope.ServiceProvider.GetRequiredService<OrderingDbContext>().AdmissionOutbox.AnyAsync(r => r.OrderId == id)) return;
        }
        throw new TimeoutException("Legacy order was not found after full sweeps.");
    }
    private async Task DueAsync(Guid order)
    {
        using var scope = _orders.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<OrderingDbContext>().AdmissionOutbox.Where(r => r.OrderId == order && r.State == "PENDING")
            .ExecuteUpdateAsync(set => set.SetProperty(r => r.NextAttemptAtUtc, DateTime.UtcNow.AddSeconds(-1)));
    }
    private async Task<Guid> LegacyAsync(bool ready = false, Guid? forcedId = null)
    {
        using var scope = _orders.Services.CreateScope(); var options = scope.ServiceProvider.GetRequiredService<DbContextOptions<OrderingDbContext>>();
        await using var db = new OrderingDbContext(options, new ConfigurationBuilder().Build());
        var order = new Order(_tenant, _branch, OrderType.DineIn, DateTime.UtcNow);
        if (forcedId is { } id) db.Entry(order).Property(o => o.Id).CurrentValue = id;
        order.AddItem(_product, "Recovery coffee", 50000, 1); if (ready) order.MarkAsReady(DateTime.UtcNow);
        db.Orders.Add(order); await db.SaveChangesAsync();
        await db.Orders.Where(o => o.Id == order.Id).ExecuteUpdateAsync(set => set.SetProperty(o => o.CreatedAtUtc, DateTime.UtcNow.AddYears(-3)));
        return order.Id;
    }

    [OperationalInfrastructureFact]
    public async Task Kitchen_offline_then_restart_receives_committed_order_without_redis_replay()
    {
        await StopKitchenAsync(); var id = await CreateAsync();
        (await CountsAsync(id)).Tickets.Should().Be(0);
        await DispatchAsync(); (await RowAsync(id)).State.Should().Be("PENDING");
        await StartKitchenAsync(); await DueAsync(id); await DispatchAsync();
        (await RowAsync(id)).State.Should().Be("ACKED");
        var result = await CountsAsync(id); (result.Tickets, result.Items, result.Stickers).Should().Be((1, 1, 1));
        using var scope = _kitchen!.Services.CreateScope();
        var notifications = (RecordingNotifier)scope.ServiceProvider.GetRequiredService<IKitchenNotifier>();
        notifications.Stickers.Should().BeEmpty("recovery must not trigger a legacy auto-printer");
        using var client = _kitchen.As(AicosRoles.Barista, _branch);
        var db = scope.ServiceProvider.GetRequiredService<KitchenOperationsDbContext>();
        var ticket = await db.Tickets.AsNoTracking().SingleAsync(t => t.OrderId == id);
        var preview = await client.GetAsync($"/api/v1/kitchen/tickets/{ticket.Id}/sticker-preview");
        preview.StatusCode.Should().Be(HttpStatusCode.OK);
        await DueAsync(id); await DispatchAsync();
        (await CountsAsync(id)).Should().Be(result);
        notifications.Stickers.Should().BeEmpty();
    }

    [OperationalInfrastructureFact]
    public async Task Lost_ack_and_service_restart_replay_the_same_receipt_and_ticket()
    {
        await StopKitchenAsync(); var id = await CreateAsync(); await StartKitchenAsync();
        _transport.LoseAckOrder = id; await DispatchAsync();
        (await RowAsync(id)).State.Should().Be("PENDING"); var before = await CountsAsync(id);
        await StopKitchenAsync(); await _orders.DisposeAsync(); StartOrdering(); await StartKitchenAsync();
        await DueAsync(id); await DispatchAsync();
        (await RowAsync(id)).State.Should().Be("ACKED"); (await CountsAsync(id)).Should().Be(before);
    }

    [OperationalInfrastructureFact]
    public async Task Two_dispatchers_and_legacy_redis_do_not_duplicate_ticket_items_or_stickers()
    {
        await StopKitchenAsync(); var id = await CreateAsync(); var payload = await PayloadAsync(id); await StartKitchenAsync();
        using var first = _orders.Services.CreateScope(); using var second = _orders.Services.CreateScope();
        var message = new OrderCreatedIntegrationEvent(_tenant, id, payload.Creation.OrderCode, _branch, null, null, null, null,
            "DineIn", 50000, false, null, [new OrderItemDto(_product, "Recovery coffee", 50000, 1, "Less ice")], DateTime.UtcNow);
        await Task.WhenAll(first.ServiceProvider.GetRequiredService<KitchenAdmissionDispatcher>().DispatchOnceAsync(),
            second.ServiceProvider.GetRequiredService<KitchenAdmissionDispatcher>().DispatchOnceAsync(),
            _orders.Services.GetRequiredService<IEventBus>().PublishAsync(message));
        await Task.Delay(200);
        var count = await CountsAsync(id); (count.Tickets, count.Items, count.Stickers).Should().Be((1, 1, 1));
        (await RowAsync(id)).State.Should().Be("ACKED");
    }

    [OperationalInfrastructureFact]
    public async Task Reconciliation_recovers_old_orders_and_persists_a_cursor_across_batches()
    {
        await StopKitchenAsync();
        using (var scope = _orders.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<OrderingDbContext>().AdmissionReconcile.ExecuteUpdateAsync(set =>
                set.SetProperty(s => s.UpperOrderId, (Guid?)null).SetProperty(s => s.LastOrderId, (Guid?)null)
                    .SetProperty(s => s.LeaseToken, (Guid?)null).SetProperty(s => s.LeaseUntilUtc, (DateTime?)null));
        var old = await LegacyAsync();
        for (var i = 0; i < 101; i++) await LegacyAsync();
        await ReconcileAsync();
        using (var scope = _orders.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<OrderingDbContext>().AdmissionReconcile.SingleAsync()).LastOrderId.Should().NotBeNull();
        var behind = await LegacyAsync(forcedId: Guid.Parse("00000000-0000-0000-0000-000000000001"));
        await ReconcileAsync(); await ReconcileAsync(); await ReconcileAsync();
        (await RowAsync(behind)).Origin.Should().Be("RECONCILE"); (await RowAsync(old)).Origin.Should().Be("RECONCILE");
        await StartKitchenAsync();
        for (var i = 0; i < 8 && (await RowAsync(old)).State != "ACKED"; i++) await DispatchAsync();
        (await CountsAsync(old)).Tickets.Should().Be(1);
    }

    [OperationalInfrastructureFact]
    public async Task Hold_blocks_queue_direct_preparation_remake_and_stickers_until_delivery_evidence()
    {
        var id = await LegacyAsync(ready: true); await EnsureBackfilledAsync(id);
        for (var i = 0; i < 10 && (await CountsAsync(id)).Tickets == 0; i++) await DispatchAsync();
        var result = await CountsAsync(id); result.Hold.Should().Be("admission.preparation_unknown");
        (await RowAsync(id)).State.Should().Be("NEEDS_REVIEW");
        using var scope = _kitchen!.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<KitchenOperationsDbContext>();
        var ticket = await db.Tickets.SingleAsync(t => t.OrderId == id);
        using var bar = _kitchen.As(AicosRoles.Barista, _branch);
        var queue = await bar.GetFromJsonAsync<JsonElement>("/api/v1/kitchen/queue");
        queue.EnumerateArray().Any(t => t.Id() == ticket.Id).Should().BeFalse();
        await bar.Put($"/api/v1/kitchen/tickets/{ticket.Id}/status", new { status = "Brewing" }).ShouldBe(409, "ticket.recovery_review_required");
        await bar.Post($"/api/v1/kitchen/tickets/{ticket.Id}/remake", new { reason = "Cannot bypass" }).ShouldBe(409, "ticket.recovery_review_required");
        await bar.GetAsync($"/api/v1/kitchen/tickets/{ticket.Id}/stickers").ShouldBe(409, "ticket.recovery_review_required");
        using (var source = _orders.Services.CreateScope())
        {
            var ordering = source.ServiceProvider.GetRequiredService<OrderingDbContext>(); var order = await ordering.Orders.SingleAsync(o => o.Id == id);
            order.Deliver(Guid.NewGuid(), DateTime.UtcNow); await ordering.SaveChangesAsync();
        }
        for (var i = 0; i < 6 && (await RowAsync(id)).State != "ACKED"; i++) { await ReconcileAsync(); await DispatchAsync(); }
        var completed = await CountsAsync(id); completed.Status.Should().Be("Completed"); completed.Hold.Should().BeNull();
        completed.Stickers.Should().Be(0); // Never re-prepares or prints recovered delivered food.
    }

    [OperationalInfrastructureFact]
    public async Task Machine_auth_rejects_staff_wrong_scope_wrong_tenant_expired_and_bad_signature()
    {
        await StopKitchenAsync(); var id = await CreateAsync(); var payload = await PayloadAsync(id); await StartKitchenAsync();
        const string path = "/internal/v1/kitchen/order-admissions";
        using var staff = _kitchen!.As(AicosRoles.Cashier, _branch); await staff.Post(path, payload).ShouldBe(401);
        using var client = _kitchen.CreateClient(); await client.Post(path, payload).ShouldBe(401);
        foreach (var token in new[] {
            AdmissionMachineAuthentication.Token(_key, _tenant, _branch, "kitchen.admission.write", DateTime.UtcNow.AddMinutes(-5)),
            AdmissionMachineAuthentication.Token(new string('x', 48), _tenant, _branch, "kitchen.admission.write", DateTime.UtcNow) })
        { client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token); await client.Post(path, payload).ShouldBe(401); }
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdmissionMachineAuthentication.Token(_key, _tenant, _branch, "kitchen.admission.lookup", DateTime.UtcNow));
        await client.Post(path, payload).ShouldBe(403);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdmissionMachineAuthentication.Token(_key, Guid.NewGuid(), _branch, "kitchen.admission.write", DateTime.UtcNow));
        await client.Post(path, payload).ShouldBe(403);
        (await CountsAsync(id)).Tickets.Should().Be(0);
    }

    [OperationalInfrastructureFact]
    public async Task Conflicting_receipt_or_payload_never_resets_an_existing_ticket()
    {
        await StopKitchenAsync(); var id = await CreateAsync(); var payload = await PayloadAsync(id); await StartKitchenAsync();
        (await _transport.SendAsync(payload, default)).Status.Should().Be(200);
        var changed = payload with { CreationHash = new string('0', 64) };
        (await _transport.SendAsync(changed, default)).Status.Should().Be(409);
        var otherOrder = Guid.NewGuid();
        var conflict = payload with { OrderId = otherOrder, CreationHash = Convert.ToHexString(AdmissionProtocol.Hash(_tenant, _branch, otherOrder, payload.Creation)) };
        (await _transport.SendAsync(conflict, default)).Status.Should().Be(409);
        (await _transport.SendAsync(payload with { EventId = Guid.NewGuid() }, default)).Status.Should().Be(200);
        var counts = await CountsAsync(id); (counts.Tickets, counts.Items, counts.Stickers).Should().Be((1, 1, 1));
    }

    [OperationalInfrastructureFact]
    public async Task Outbox_failure_rolls_back_order_and_no_partial_capture_is_committed()
    {
        await StopKitchenAsync(); _captureFailure.Armed = true;
        await Assert.ThrowsAnyAsync<Exception>(() => CreateAsync());
        _captureFailure.Armed.Should().BeFalse();
        using var scope = _orders.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        (await db.Orders.CountAsync(o => o.BranchId == _branch)).Should().Be(0);
        (await db.AdmissionOutbox.CountAsync(o => o.BranchId == _branch)).Should().Be(0);
    }

    [OperationalInfrastructureFact]
    public async Task Pos_qr_and_web_pickup_capture_the_same_atomic_admission_contract()
    {
        var pos = await CreateAsync(); (await RowAsync(pos)).Origin.Should().Be("CAPTURE");
        var qr = _tables.Add(_tenant, _branch, "RECOVERY"); using var guest = _orders.CreateClient();
        var scan = await guest.PostOk("/api/v1/orders/qr/scan", new { qrToken = qr });
        guest.DefaultRequestHeaders.Add("X-Table-Session", scan.GetProperty("sessionToken").GetString());
        await guest.Post("/api/v1/orders/qr/cart/items", new { productId = _product, quantity = 1 }).ShouldBe(200);
        var checkout = await guest.PostOk("/api/v1/orders/qr/checkout", new { }); (await RowAsync(checkout.Id())).Origin.Should().Be("CAPTURE");
        using var customer = _orders.As(AicosRoles.Customer);
        var date = DateTime.UtcNow.AddHours(7).Date.AddDays(1).ToString("yyyy-MM-dd");
        var slots = await customer.GetFromJsonAsync<JsonElement>($"/api/v1/orders/pickup-slots/available?branchId={_branch}&date={date}");
        var slot = slots.GetProperty("slots").EnumerateArray().First(s => s.GetProperty("available").GetBoolean()).GetProperty("startUtc").GetDateTime();
        var pickup = await customer.PostOk("/api/v1/orders/pickup", new { branchId = _branch, slotStartUtc = slot, items = new[] { new { productId = _product, quantity = 1 } } });
        (await RowAsync(pickup.Id())).Origin.Should().Be("CAPTURE");
    }

    [OperationalInfrastructureFact]
    public async Task Ordering_process_killed_after_commit_recovers_without_any_publish()
    {
        var id = await KillProbeAsync("ordering-after-commit", new[] { _tenant, _branch, _product });
        (await RowAsync(id)).State.Should().Be("PENDING"); (await CountsAsync(id)).Tickets.Should().Be(0);
        for (var i = 0; i < 10 && (await RowAsync(id)).State != "ACKED"; i++) await DispatchAsync();
        (await RowAsync(id)).State.Should().Be("ACKED"); (await CountsAsync(id)).Tickets.Should().Be(1);
    }

    [OperationalInfrastructureFact]
    public async Task Kitchen_process_killed_before_commit_rolls_back_then_retry_admits_once()
    {
        await StopKitchenAsync(); var id = await CreateAsync(); var payload = await PayloadAsync(id);
        await KillProbeAsync("kitchen-before-commit", payload); (await CountsAsync(id)).Tickets.Should().Be(0);
        await StartKitchenAsync(); for (var i = 0; i < 10 && (await RowAsync(id)).State != "ACKED"; i++) await DispatchAsync();
        (await CountsAsync(id)).Tickets.Should().Be(1);
    }

    [OperationalInfrastructureFact]
    public async Task Kitchen_process_killed_after_commit_reuses_ticket_without_duplicate_stickers()
    {
        await StopKitchenAsync(); var id = await CreateAsync(); var payload = await PayloadAsync(id);
        await KillProbeAsync("kitchen-after-commit", payload); var committed = await CountsAsync(id); committed.Tickets.Should().Be(1);
        await StartKitchenAsync(); for (var i = 0; i < 10 && (await RowAsync(id)).State != "ACKED"; i++) await DispatchAsync();
        (await CountsAsync(id)).Should().Be(committed); (await RowAsync(id)).State.Should().Be("ACKED");
    }

    [OperationalInfrastructureFact]
    public async Task Durable_http_admission_works_while_redis_is_stopped_and_kitchen_starts_offline()
    {
        await StopKitchenAsync(); var id = await CreateAsync();
        await OperationalTestTarget.DuringRedisOutageAsync(async () =>
        {
            await StartKitchenAsync();
            for (var i = 0; i < 10 && (await RowAsync(id)).State != "ACKED"; i++) await DispatchAsync();
            (await RowAsync(id)).State.Should().Be("ACKED"); (await CountsAsync(id)).Tickets.Should().Be(1);
            await Task.Delay(6000); // Longer than initial Redis connect timeout; HTTP host must remain alive.
            using var client = _kitchen!.CreateClient(); await client.GetAsync("/health/live").ShouldBe(200);
        });
    }

    [OperationalInfrastructureFact]
    public async Task Hosted_dispatcher_automatically_recovers_after_kitchen_returns()
    {
        await StopKitchenAsync(); await _orders.DisposeAsync(); _automatic = true;
        const string setting = "KitchenAdmission__DispatcherEnabled";
        _prior[setting] = Environment.GetEnvironmentVariable(setting); Environment.SetEnvironmentVariable(setting, "true");
        StartOrdering(); var id = await CreateAsync(); await StartKitchenAsync();
        var until = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < until && (await RowAsync(id)).State != "ACKED") await Task.Delay(200);
        (await RowAsync(id)).State.Should().Be("ACKED"); (await CountsAsync(id)).Tickets.Should().Be(1);
    }

    [OperationalInfrastructureFact]
    public async Task Expired_dispatcher_lease_fences_the_old_ack_and_has_no_database_transaction_during_http()
    {
        await StopKitchenAsync(); var id = await CreateAsync(); await StartKitchenAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        _transport.BeforeSend = async request =>
        {
            if (request.OrderId != id || Interlocked.Increment(ref calls) != 1) return;
            await using var probe = new NpgsqlConnection(_ordering); await probe.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND pid <> pg_backend_pid() AND state = 'idle in transaction'", probe);
            ((long)(await command.ExecuteScalarAsync())!).Should().Be(0);
            entered.TrySetResult(); await release.Task;
        };
        using var first = _orders.Services.CreateScope();
        var old = first.ServiceProvider.GetRequiredService<KitchenAdmissionDispatcher>().DispatchOnceAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            using var scope = _orders.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            await db.AdmissionOutbox.Where(r => r.OrderId == id).ExecuteUpdateAsync(set => set.SetProperty(r => r.LeaseUntilUtc, DateTime.UtcNow.AddSeconds(-1)));
            await DispatchAsync(); var winner = await RowAsync(id); winner.State.Should().Be("ACKED");
            release.TrySetResult(); await old;
            (await RowAsync(id)).AcknowledgedAtUtc.Should().Be(winner.AcknowledgedAtUtc);
            var counts = await CountsAsync(id); (counts.Tickets, counts.Items, counts.Stickers).Should().Be((1, 1, 1));
        }
        finally { release.TrySetResult(); await old; }
    }

    [OperationalInfrastructureFact]
    public async Task Failure_at_transaction_commit_rolls_back_both_persisted_order_and_outbox()
    {
        await StopKitchenAsync(); _commitFailure.Armed = true;
        await Assert.ThrowsAnyAsync<Exception>(() => CreateAsync()); _commitFailure.Armed.Should().BeFalse();
        using var scope = _orders.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        (await db.Orders.CountAsync(o => o.BranchId == _branch)).Should().Be(0);
        (await db.AdmissionOutbox.CountAsync(o => o.BranchId == _branch)).Should().Be(0);
    }

    [OperationalInfrastructureFact]
    public async Task Replaying_old_admission_never_resets_ready_or_delivered_ticket()
    {
        await StopKitchenAsync(); var id = await CreateAsync(); var old = await PayloadAsync(id); await StartKitchenAsync();
        (await _transport.SendAsync(old, default)).Status.Should().Be(200);
        using var scope = _kitchen!.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<KitchenOperationsDbContext>();
        var ticket = await db.Tickets.AsNoTracking().SingleAsync(t => t.OrderId == id);
        using var bar = _kitchen.As(AicosRoles.Barista, _branch);
        await bar.Put($"/api/v1/kitchen/tickets/{ticket.Id}/status", new { status = "ReadyForPickup" }).ShouldBe(200);
        (await _transport.SendAsync(old with { EventId = Guid.NewGuid() }, default)).Status.Should().Be(200);
        (await CountsAsync(id)).Status.Should().Be("ReadyForPickup");
        using (var source = _orders.Services.CreateScope())
        {
            var ordering = source.ServiceProvider.GetRequiredService<OrderingDbContext>(); var order = await ordering.Orders.SingleAsync(o => o.Id == id);
            order.MarkAsReady(DateTime.UtcNow); order.Deliver(Guid.NewGuid(), DateTime.UtcNow); await ordering.SaveChangesAsync();
        }
        (await _transport.SendAsync(await PayloadAsync(id), default)).Status.Should().Be(200);
        var completed = await CountsAsync(id); completed.Status.Should().Be("Completed");
        (await _transport.SendAsync(old, default)).Status.Should().Be(200);
        (await CountsAsync(id)).Should().Be(completed);
    }

    [OperationalInfrastructureFact]
    public async Task Retrying_public_create_keeps_one_order_and_one_stable_admission()
    {
        await StopKitchenAsync(); var key = Guid.NewGuid().ToString("N");
        var first = await CreateAsync(key); var original = await RowAsync(first);
        (await CreateAsync(key)).Should().Be(first);
        var replay = await RowAsync(first); replay.EventId.Should().Be(original.EventId);
        replay.CreationSha256.Should().Equal(original.CreationSha256);
        using var scope = _orders.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        (await db.Orders.CountAsync(o => o.BranchId == _branch)).Should().Be(1);
        (await db.AdmissionOutbox.CountAsync(o => o.BranchId == _branch)).Should().Be(1);
    }

    private static async Task<Guid> KillProbeAsync(string mode, object input)
    {
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        info.ArgumentList.Add(OperationalTestTarget.Required("AICOS_OPERATIONS_CRASH_PROBE")); info.ArgumentList.Add(mode);
        using var process = Process.Start(info)!;
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(input, AdmissionProtocol.Json)); process.StandardInput.Close();
            var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
            if (line is null || !line.StartsWith("READY:")) throw new InvalidOperationException("Crash probe did not reach guarded boundary.");
            var id = Guid.Parse(line[6..]); process.Kill(entireProcessTree: true); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); return id;
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } await error; }
    }

    private sealed class RecoveryTransport(Func<HttpClient?> target, string key) : IKitchenAdmissionTransport
    {
        public Guid? LoseAckOrder;
        public Func<AdmissionRequest, Task>? BeforeSend;
        private HttpKitchenAdmissionTransport Live(HttpClient client) => new(client, new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["KitchenAdmission:MachineKey"] = key }).Build());
        public async Task<AdmissionTransportResult> SendAsync(AdmissionRequest request, CancellationToken ct)
        {
            if (BeforeSend is not null) await BeforeSend(request);
            using var client = target() ?? throw new HttpRequestException("Kitchen offline.");
            var result = await Live(client).SendAsync(request, ct);
            if (LoseAckOrder == request.OrderId) { LoseAckOrder = null; throw new TaskCanceledException("Test lost ACK after real commit."); }
            return result;
        }
        public async Task<IReadOnlyList<AdmissionPresence>> LookupAsync(Guid tenant, Guid branch, IReadOnlyList<Guid> orders, CancellationToken ct)
        { using var client = target() ?? throw new HttpRequestException("Kitchen offline."); return await Live(client).LookupAsync(tenant, branch, orders, ct); }
    }
    private sealed class CaptureFailure : SaveChangesInterceptor
    {
        public bool Armed;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        { if (Armed && data.Context!.ChangeTracker.Entries<AdmissionOutbox>().Any(e => e.State == EntityState.Added)) { Armed = false; throw new InvalidOperationException("Test capture failure."); } return ValueTask.FromResult(result); }
    }
    private sealed class CommitFailure : DbTransactionInterceptor
    {
        public bool Armed;
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData data, InterceptionResult result, CancellationToken ct = default)
        { if (Armed) { Armed = false; throw new InvalidOperationException("Test commit failure after SQL writes."); } return ValueTask.FromResult(result); }
    }
}
