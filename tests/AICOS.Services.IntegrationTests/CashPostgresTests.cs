using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;
using AICOS.Payments.Api;
using AICOS.Payments.Api.Domain;
using AICOS.Payments.Api.Infrastructure;
using AICOS.Payments.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace AICOS.Services.IntegrationTests;

public sealed class CashPostgresFactAttribute : FactAttribute
{
    public CashPostgresFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AICOS_CASH_TEST_CONNECTION")))
            Skip = "Run scripts/Test-CashCollectionPostgres.ps1 for the isolated PostgreSQL cluster.";
    }
}

/// <summary>Real PostgreSQL and HTTP handlers. Identity/Ordering/Redis are explicit test doubles; JWTs are test-signed.</summary>
public sealed class CashPostgresTests : IAsyncLifetime
{
    private ServiceFactory<PaymentsService, PaymentsDbContext> _factory = null!;
    private readonly FakeOrderDirectory _orders = new();
    private readonly SaveFault _save = new();
    private readonly CommitFault _commit = new();
    private Guid _branch, _cashier = Guid.NewGuid();
    private HttpClient _client = null!;
    private const string Cash = "/api/v1/payments/transactions/cash";

    public async Task InitializeAsync()
    {
        var connection = Environment.GetEnvironmentVariable("AICOS_CASH_TEST_CONNECTION") ?? throw new InvalidOperationException("Missing scratch connection.");
        var parsed = new NpgsqlConnectionStringBuilder(connection);
        if (parsed.Database != "aicos_cashflow_test" || parsed.Host != "127.0.0.1" || parsed.Port == 5432 || parsed.Port == 5433)
            throw new InvalidOperationException("Refusing a non-isolated test database.");
        await using (var probe = new NpgsqlConnection(connection))
        {
            await probe.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT system_identifier::text FROM pg_control_system()", probe);
            var actual = (string)(await command.ExecuteScalarAsync())!;
            actual.Should().Be(Environment.GetEnvironmentVariable("AICOS_CASH_TEST_CLUSTER"), "cluster guard must pass before test DDL/DML");
        }
        _factory = new(services =>
        {
            services.RemoveAll<IOrderDirectory>();
            services.AddSingleton<IOrderDirectory>(_orders);
            foreach (var job in services.Where(s => s.ImplementationType == typeof(PaymentExpiryJob)).ToList()) services.Remove(job);
        }, connection, options => options.AddInterceptors(_save, _commit));
        using (var scope = _factory.Services.CreateScope())
        {
            // Only the empty scratch cluster validated above; no migration/EnsureDeleted or connection to main.
            await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Database.EnsureCreatedAsync();
        }
        _branch = _factory.Branches.Add(ServiceFactory<PaymentsService, PaymentsDbContext>.DefaultTenantId);
        _client = _factory.As(AicosRoles.Cashier, _branch, _cashier);
    }
    public Task DisposeAsync() { _client?.Dispose(); _factory?.Dispose(); return Task.CompletedTask; }
    private Guid Order() { var id = Guid.NewGuid(); _orders.Set(id, _branch, 50000); return id; }
    private async Task<HttpResponseMessage> Pay(Guid order, string key, int tendered = 100000, HttpClient? client = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Cash) { Content = JsonContent.Create(new { orderId = order, branchId = _branch, amount = 50000, amountTendered = tendered }) };
        request.Headers.Add("Idempotency-Key", key);
        return await (client ?? _client).SendAsync(request);
    }
    private async Task<List<Payment>> Rows(Guid order)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Payments.AsNoTracking().Where(p => p.OrderId == order).ToListAsync();
    }
    [CashPostgresFact]
    public async Task Sixteen_parallel_replays_persist_one_cash_payment()
    {
        var order = Order();
        var responses = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Pay(order, "same-key")));
        responses.Count(r => r.StatusCode == HttpStatusCode.Created).Should().Be(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(15);
        var receipt = await responses.Single(r => r.StatusCode == HttpStatusCode.Created).Content.ReadFromJsonAsync<JsonElement>();
        receipt.GetProperty("payment").GetProperty("amountTendered").GetDecimal().Should().Be(100000);
        receipt.GetProperty("payment").GetProperty("change").GetDecimal().Should().Be(50000);
        var rows = await Rows(order);
        rows.Should().ContainSingle(); rows[0].CashierUserId.Should().Be(_cashier);
        rows[0].Status.Should().Be(PaymentStatus.Succeeded);
        _factory.Events.Published.OfType<CashPaymentRecordedEvent>().Should().ContainSingle();
    }
    [CashPostgresFact]
    public async Task Different_keys_and_cashiers_cannot_record_cash_twice()
    {
        var order = Order();
        using var secondCashier = _factory.As(AicosRoles.Cashier, _branch, Guid.NewGuid());
        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Pay(order, "key-" + i, client: i % 2 == 0 ? _client : secondCashier)));
        responses.Count(r => r.StatusCode == HttpStatusCode.Created).Should().Be(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).Should().Be(11);
        (await Rows(order)).Should().ContainSingle();
    }
    [CashPostgresFact]
    public async Task Replay_after_order_sync_is_safe_and_different_payload_or_order_is_rejected()
    {
        var order = Order(); var key = Guid.NewGuid().ToString();
        var first = await (await Pay(order, key)).Content.ReadFromJsonAsync<JsonElement>();
        _orders.Set(order, _branch, 50000, paymentStatus: "Paid");
        var replay = await Pay(order, key); replay.StatusCode.Should().Be(HttpStatusCode.OK);
        (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("payment").GetProperty("id")
            .GetGuid().Should().Be(first.GetProperty("payment").GetProperty("id").GetGuid());
        (await Pay(order, key, 60000)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        var second = Order(); (await Pay(second, key)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Rows(second)).Should().BeEmpty();
    }
    [CashPostgresFact]
    public async Task Cash_and_provider_success_serialize_without_silently_replacing_pending_transfer()
    {
        var order = Order(); var tenant = ServiceFactory<PaymentsService, PaymentsDbContext>.DefaultTenantId;
        Guid paymentId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            var pending = new Payment(tenant, _branch, order, 50000, "Mock", "Test transfer");
            db.Payments.Add(pending); await db.SaveChangesAsync(); paymentId = pending.Id;
        }
        async Task Confirm()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            await scope.ServiceProvider.GetRequiredService<PaymentProcessor>().ApplySuccessAsync((await db.Payments.FindAsync(paymentId))!, "test-receipt", default);
        }
        var cash = Pay(order, "cash-vs-provider"); await Confirm();
        (await cash).StatusCode.Should().Be(HttpStatusCode.Conflict);
        var rows = await Rows(order); rows.Should().ContainSingle(); rows[0].IsCash.Should().BeFalse(); rows[0].IsPaid.Should().BeTrue();
    }
    [CashPostgresFact]
    public async Task Parallel_provider_notifications_apply_one_success_event()
    {
        var order = Order(); Guid id;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            var payment = new Payment(ServiceFactory<PaymentsService, PaymentsDbContext>.DefaultTenantId, _branch, order, 50000, "Mock", "Test");
            db.Payments.Add(payment); await db.SaveChangesAsync(); id = payment.Id;
        }
        await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ =>
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            await scope.ServiceProvider.GetRequiredService<PaymentProcessor>().ApplySuccessAsync((await db.Payments.FindAsync(id))!, "receipt", default);
        }));
        _factory.Events.Published.OfType<PaymentSucceededEvent>().Should().ContainSingle();
    }
    [CashPostgresFact]
    public async Task Transient_failure_after_save_rolls_back_and_retries_without_duplicate_cash()
    {
        var order = Order(); _save.Armed = 1;
        (await Pay(order, "rollback-retry")).StatusCode.Should().Be(HttpStatusCode.Created);
        (await Rows(order)).Should().ContainSingle();
        _factory.Events.Published.OfType<CashPaymentRecordedEvent>().Should().ContainSingle();
    }
    [CashPostgresFact]
    public async Task Lost_commit_acknowledgement_reuses_the_committed_payment()
    {
        var order = Order(); _commit.Armed = 1;
        (await Pay(order, "lost-commit")).StatusCode.Should().Be(HttpStatusCode.Created);
        (await Rows(order)).Should().ContainSingle();
        _factory.Events.Published.OfType<CashPaymentRecordedEvent>().Should().ContainSingle();
    }
    [CashPostgresFact]
    public async Task Success_cannot_be_overwritten_by_stale_expiry_failure_or_cancellation()
    {
        var order = Order(); Guid id;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            var p = new Payment(ServiceFactory<PaymentsService, PaymentsDbContext>.DefaultTenantId, _branch, order, 50000, "Mock", "Race test");
            p.SetCheckout(null, DateTime.UtcNow.AddHours(-1)); db.Payments.Add(p); await db.SaveChangesAsync(); id = p.Id;
        }
        async Task Mutate(string action)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            var p = (await db.Payments.FindAsync(id))!;
            var processor = scope.ServiceProvider.GetRequiredService<PaymentProcessor>();
            if (action == "success") await processor.ApplySuccessAsync(p, "real-test-receipt", default);
            else if (action == "failure") await processor.ApplyFailureAsync(p, "test decline", default);
            else await processor.ExpireIfDueAsync(p, default);
        }
        await Task.WhenAll(Mutate("success"), Mutate("failure"), Mutate("expire"),
            _client.PostAsJsonAsync($"/api/v1/payments/transactions/{id}/cancel", new { }));
        var rows = await Rows(order); rows.Should().ContainSingle(); rows[0].Status.Should().Be(PaymentStatus.Succeeded);
        _factory.Events.Published.OfType<PaymentSucceededEvent>().Should().ContainSingle();
    }

    [CashPostgresFact]
    public async Task Known_provider_receipt_requires_reconciliation_instead_of_another_cash_collection()
    {
        var order = Order();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            var p = new Payment(ServiceFactory<PaymentsService, PaymentsDbContext>.DefaultTenantId, _branch, order, 50000, "Mock", "Lost provider confirmation");
            p.SetProviderState(ProviderState.Succeeded); p.MarkExpired(); db.Payments.Add(p); await db.SaveChangesAsync();
        }
        (await Pay(order, "do-not-recollect")).StatusCode.Should().Be(HttpStatusCode.Conflict);
        var rows = await Rows(order); rows.Should().ContainSingle(); rows[0].IsCash.Should().BeFalse();
        var snapshot = await _client.GetFromJsonAsync<JsonElement>($"/api/v1/payments/orders/{order}/settlement");
        snapshot.GetProperty("state").GetString().Should().Be("ReconciliationRequired");
    }
    [CashPostgresFact]
    public async Task Cash_and_online_creation_cannot_both_start_collection()
    {
        var order = Order();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            if (!await db.PaymentProviders.AnyAsync(p => p.Code == "Mock"))
            {
                var provider = new PaymentProvider("Mock", "Scratch-only provider", ProviderMode.Mock);
                provider.SetEnabled(true); db.PaymentProviders.Add(provider); await db.SaveChangesAsync();
            }
        }
        var responses = await Task.WhenAll(Pay(order, "cash-vs-online-start"),
            _client.PostAsJsonAsync("/api/v1/payments/transactions", new { orderId = order, branchId = _branch, amount = 50000, providerCode = "Mock" }));
        responses.Count(r => r.StatusCode == HttpStatusCode.Created).Should().Be(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).Should().Be(1);
        (await Rows(order)).Should().ContainSingle();
    }

    [CashPostgresFact]
    public async Task One_request_key_cannot_concurrently_collect_for_two_different_orders()
    {
        var first = Order(); var second = Order();
        var responses = await Task.WhenAll(Pay(first, "one-request-key"), Pay(second, "one-request-key"));
        responses.Count(r => r.StatusCode == HttpStatusCode.Created).Should().Be(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).Should().Be(1);
        ((await Rows(first)).Count + (await Rows(second)).Count).Should().Be(1);
    }
    [CashPostgresFact]
    public async Task Sixteen_online_retries_share_one_committed_attempt()
    {
        var order = Order();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            if (!await db.PaymentProviders.AnyAsync(p => p.Code == "Mock"))
            {
                var provider = new PaymentProvider("Mock", "Scratch provider", ProviderMode.Mock);
                provider.SetEnabled(true); db.PaymentProviders.Add(provider); await db.SaveChangesAsync();
            }
        }
        var request = new { orderId = order, branchId = _branch, amount = 50000, providerCode = "Mock" };
        var responses = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            _client.PostAsJsonAsync("/api/v1/payments/transactions", request)));
        responses.Count(r => r.StatusCode == HttpStatusCode.Created).Should().Be(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(15);
        var rows = await Rows(order);
        rows.Should().ContainSingle();
        foreach (var response in responses)
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid().Should().Be(rows[0].Id);
    }

    [CashPostgresFact]
    public async Task Expired_unverified_transfer_blocks_parallel_cash_requests()
    {
        var order = Order();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            var payment = new Payment(ServiceFactory<PaymentsService, PaymentsDbContext>.DefaultTenantId,
                _branch, order, 50000, "Mock", "Expired without provider evidence");
            payment.MarkExpired(); db.Payments.Add(payment); await db.SaveChangesAsync();
        }
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Pay(order, "expired-" + i)));
        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Conflict);
        (await Rows(order)).Should().ContainSingle();
    }

    private sealed class SaveFault : SaveChangesInterceptor
    {
        public int Armed;
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData data, int result, CancellationToken ct = default)
        {
            if (Interlocked.Exchange(ref Armed, 0) == 1) throw new NpgsqlException("Injected scratch-only connection failure", new IOException("test"));
            return ValueTask.FromResult(result);
        }
    }
    private sealed class CommitFault : DbTransactionInterceptor
    {
        public int Armed;
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData data, CancellationToken ct = default)
        {
            if (Interlocked.Exchange(ref Armed, 0) == 1) throw new NpgsqlException("Injected scratch-only lost commit acknowledgement", new IOException("test"));
            return Task.CompletedTask;
        }
    }
}
