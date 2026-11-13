using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Billing;
using AICOS.Contracts.Events;
using AICOS.KitchenOperations.Api;
using AICOS.KitchenOperations.Api.Persistence;
using AICOS.Ordering.Api;
using AICOS.Ordering.Api.Persistence;
using AICOS.Ordering.Api.Services;
using AICOS.Ordering.Api.Features.SessionBilling;
using AICOS.Payments.Api;
using AICOS.Payments.Api.Persistence;
using AICOS.Payments.Api.Features.SessionBilling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace AICOS.Services.IntegrationTests;

/// <summary>Real HTTP service handlers/serialization; test JWT, Menu/Table directories and event delivery are explicit doubles.</summary>
public sealed class SessionBillHarness : IDisposable
{
    public readonly Guid Tenant = ServiceFactory<OrderingService, OrderingDbContext>.DefaultTenantId;
    public readonly Guid Branch = Guid.NewGuid(), CashierId = Guid.NewGuid();
    public readonly string MachineKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    public readonly string WebhookSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    public ServiceFactory<OrderingService, OrderingDbContext> Ordering = null!;
    public ServiceFactory<PaymentsService, PaymentsDbContext> Payments = null!;
    public ServiceFactory<KitchenOperationsService, KitchenOperationsDbContext> Kitchen = null!;
    public HttpClient Cashier = null!, OrderCashier = null!;
    public Guid Session, Table, Product;
    public string Qr = "";
    public bool LosePaidAck, KitchenOffline;
    public AICOS.Payments.Api.Infrastructure.IPaymentGateway? TestGateway;
    private string? _connection;
    public Func<Task>? BeforeFreeze;
    private readonly FakeMenu _menu = new();
    private readonly FakeTableQr _tables = new();
    public IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        ["Database:MigrateOnStartup"] = "false",
        ["SessionBilling:Enabled"] = "true",
        ["SessionBilling:MachineKey"] = MachineKey,
        ["SessionBilling:AllowTestProviders"] = "true",
        ["SessionBilling:TestWebhookSecret"] = WebhookSecret,
        ["SessionBilling:DispatcherEnabled"] = "false"
    };
    private static void NoTimers(IServiceCollection services)
    {
        foreach (var d in services.Where(d => d.ImplementationType?.Name is "PickupOrderExpiryWorker" or "TableSessionExpiryWorker" or "PaymentExpiryJob" or "BillOutboxWorker").ToList()) services.Remove(d);
    }
    public async Task InitializeAsync(string? connection = null)
    {
        if (connection is not null) await GuardAsync(connection);
        _connection = connection;
        Ordering = new(services =>
        {
            services.RemoveAll<IMenuCatalogClient>(); services.AddSingleton<IMenuCatalogClient>(_menu);
            services.RemoveAll<ITableQrClient>(); services.AddSingleton<ITableQrClient>(_tables);
            services.RemoveAll<IBillSettlementClient>(); services.AddSingleton<IBillSettlementClient>(new SettlementBridge(this)); NoTimers(services);
        }, connection, settings: Settings, environmentName: "Development");
        Kitchen = new(services => NoTimers(services), connection, settings: Settings, environmentName: "Development");
        Payments = CreatePayments(connection);
        if (connection is not null)
        {
            // The target/cluster guard above is mandatory before these approved scratch migrations.
            using (var scope = Ordering.Services.CreateScope()) await scope.ServiceProvider.GetRequiredService<OrderingDbContext>().Database.MigrateAsync();
            using (var scope = Kitchen.Services.CreateScope()) await scope.ServiceProvider.GetRequiredService<KitchenOperationsDbContext>().Database.MigrateAsync();
            using (var scope = Payments.Services.CreateScope()) await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Database.MigrateAsync();
        }
        Ordering.Branches.Add(Tenant, id: Branch); Kitchen.Branches.Add(Tenant, id: Branch); Payments.Branches.Add(Tenant, id: Branch);
        Product = _menu.Add(Branch, "Bill test coffee", 50000);
        Qr = _tables.Add(Tenant, Branch, "A01"); Table = (await _tables.LookupAsync(Qr, default))!.TableId;
        OrderCashier = Ordering.As(AicosRoles.Cashier, Branch, CashierId); Cashier = Payments.As(AicosRoles.Cashier, Branch, CashierId);
        using var guest = Ordering.CreateClient();
        var scan = await (await guest.PostAsJsonAsync("/api/v1/orders/qr/scan", new { qrToken = Qr })).Content.ReadFromJsonAsync<JsonElement>();
        Session = scan.GetProperty("sessionId").GetGuid();
    }
    private ServiceFactory<PaymentsService, PaymentsDbContext> CreatePayments(string? connection) => new(services =>
    {
        services.RemoveAll<IBillOrderingClient>(); services.AddSingleton<IBillOrderingClient>(new OrderingBridge(this));
        services.RemoveAll<IBillKitchenClient>(); services.AddSingleton<IBillKitchenClient>(new KitchenBridge(this));
        if (TestGateway is not null)
        {
            services.RemoveAll<AICOS.Payments.Api.Infrastructure.GatewayRegistry>();
            services.AddSingleton(new AICOS.Payments.Api.Infrastructure.GatewayRegistry([TestGateway]));
        }
        NoTimers(services);
    }, connection, settings: Settings, environmentName: "Development");
    public Task RestartPaymentsAsync()
    {
        Cashier.Dispose(); Payments.Dispose(); Payments = CreatePayments(_connection);
        Payments.Branches.Add(Tenant, id: Branch);
        Cashier = Payments.As(AicosRoles.Cashier, Branch, CashierId);
        return Task.CompletedTask;
    }
    public async Task<HttpResponseMessage> AddRoundAsync(string? requestKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
        {
            Content = JsonContent.Create(new
            {
                type = "DineIn",
                branchId = Branch,
                tableId = Table,
                tableNumber = "A01",
                tableQrToken = Qr,
                expectedSessionId = Session,
                items = new[] { new { productId = Product, quantity = 1 } }
            })
        };
        if (requestKey is not null) request.Headers.Add("Idempotency-Key", requestKey);
        return await OrderCashier.SendAsync(request);
    }
    public async Task<Guid> RoundAsync()
    {
        var response = await AddRoundAsync(Guid.NewGuid().ToString("N"));
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await Kitchen.HandleAsync(Ordering.Events.Published.OfType<OrderCreatedIntegrationEvent>().Last(e => e.OrderId == id));
        return id;
    }
    public async Task<BillView> BillAsync() => (await Cashier.GetFromJsonAsync<BillView>($"/api/v1/payments/bills?sessionId={Session}"))!;
    public async Task<HttpResponseMessage> CollectAsync(BillView bill, string method, string? key = null, decimal? tendered = null, bool confirm = true, HttpClient? caller = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/payments/bills/{bill.Id}/payments/{method}")
        {
            Content = JsonContent.Create(new CollectBillRequest(Session, bill.Fingerprint, tendered ?? bill.Quote.Total, confirm, method == "qr" ? "Mock" : null))
        };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString("N"));
        return await (caller ?? Cashier).SendAsync(request);
    }
    public async Task<BillView> CashAsync(BillView bill, string? key = null)
    {
        var response = await CollectAsync(bill, "cash", key); response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<BillView>())!;
    }
    public async Task<HttpResponseMessage> ReceiptAsync(BillView bill, string eventId, string status = "success", decimal? amount = null, string? signature = null)
    {
        var reference = bill.Payment!.TransactionRef; var value = amount ?? bill.Quote.Total;
        return await Payments.CreateClient().PostAsJsonAsync("/api/v1/payments/bills/webhooks/Mock", new
        {
            eventId,
            transactionRef = reference,
            status,
            amount = value,
            signature = signature ?? AICOS.Payments.Api.Infrastructure.WebhookSignature.Compute(WebhookSecret, eventId, reference, status, value)
        });
    }
    public async Task ServeAsync(Guid id)
    {
        using var barista = Kitchen.As(AicosRoles.Barista, Branch);
        var queue = await barista.GetFromJsonAsync<JsonElement>("/api/v1/kitchen/queue");
        var ticket = queue.EnumerateArray().First(t => t.GetProperty("orderId").GetGuid() == id);
        foreach (var state in new[] { "Brewing", "ReadyForPickup" })
            (await barista.PutAsJsonAsync($"/api/v1/kitchen/tickets/{ticket.GetProperty("id").GetGuid()}/status", new { status = state })).EnsureSuccessStatusCode();
        await Ordering.HandleAsync(Kitchen.Events.Published.OfType<KitchenOrderReadyIntegrationEvent>().Last(e => e.OrderId == id));
        (await OrderCashier.PostAsJsonAsync($"/api/v1/orders/{id}/deliver", new { })).EnsureSuccessStatusCode();
    }
    public async Task DispatchAgainAsync()
    {
        Payments.Clock.Now = Payments.Clock.Now.AddMinutes(3);
        using var scope = Payments.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<BillOutboxDispatcher>().TryBillAsync(BillIdentity.For(Tenant, Session), default);
    }
    public static async Task GuardAsync(string connection)
    {
        var parsed = new NpgsqlConnectionStringBuilder(connection);
        if (parsed.Database != "aicos_billflow_test" || parsed.Host != "127.0.0.1" || parsed.Port is 5432 or 5433)
            throw new InvalidOperationException("Refusing non-isolated billing database.");
        await using var probe = new NpgsqlConnection(connection); await probe.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT system_identifier::text FROM pg_control_system()", probe);
        var cluster = (string)(await cmd.ExecuteScalarAsync())!;
        if (cluster != Environment.GetEnvironmentVariable("AICOS_BILL_TEST_CLUSTER")) throw new InvalidOperationException("Bill cluster guard failed.");
    }
    private HttpClient Machine<T, DB>(ServiceFactory<T, DB> factory) where T : class where DB : DbContext
    { var client = factory.CreateClient(); client.DefaultRequestHeaders.Add(BillingMachineAuth.Header, MachineKey); return client; }
    private sealed class OrderingBridge(SessionBillHarness h) : IBillOrderingClient
    {
        public async Task<SessionBillQuote> QuoteAsync(Guid session, CancellationToken ct) =>
            (await h.OrderCashier.GetFromJsonAsync<SessionBillQuote>($"/api/v1/orders/qr/sessions/{session}/billing-quote", ct))!;
        public async Task<SessionBillQuote> FreezeAsync(Guid session, FreezeBillRequest request, CancellationToken ct)
        {
            if (h.BeforeFreeze is { } hook) { h.BeforeFreeze = null; await hook(); }
            using var client = h.Machine(h.Ordering); var response = await client.PostAsJsonAsync($"/internal/session-billing/sessions/{session}/freeze", request, ct);
            if (!response.IsSuccessStatusCode) throw new ConflictException("Test freeze refused stale snapshot.", "billing.stale");
            return (await response.Content.ReadFromJsonAsync<SessionBillQuote>(cancellationToken: ct))!;
        }
        public async Task<bool> PaidAsync(BillPaidNotice notice, CancellationToken ct)
        {
            using var client = h.Machine(h.Ordering); var response = await client.PostAsJsonAsync("/internal/session-billing/paid", notice, ct);
            if (h.LosePaidAck) { h.LosePaidAck = false; throw new HttpRequestException("Controlled lost ACK after commit"); }
            return response.IsSuccessStatusCode;
        }
    }
    private sealed class KitchenBridge(SessionBillHarness h) : IBillKitchenClient
    {
        public async Task<bool> PaidAsync(KitchenBillPaidNotice notice, CancellationToken ct)
        {
            if (h.KitchenOffline) throw new HttpRequestException("Controlled Kitchen offline");
            using var client = h.Machine(h.Kitchen); return (await client.PostAsJsonAsync("/internal/session-billing/paid", notice, ct)).IsSuccessStatusCode;
        }
    }
    private sealed class SettlementBridge(SessionBillHarness h) : IBillSettlementClient
    {
        public async Task<BillCloseProof?> ProofAsync(Guid id, CancellationToken ct)
        {
            using var client = h.Machine(h.Payments); var response = await client.GetAsync($"/internal/session-billing/bills/{id}/proof", ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<BillCloseProof>(cancellationToken: ct) : null;
        }
        public async Task<BillCloseProof> SealAsync(BillCloseProof proof, CancellationToken ct)
        {
            using var client = h.Machine(h.Payments); var response = await client.PostAsJsonAsync($"/internal/session-billing/bills/{proof.BillId}/seal", new BillSealRequest(proof), ct);
            if (!response.IsSuccessStatusCode) throw new ConflictException("Test settlement refused.", "billing.not_settled");
            return (await response.Content.ReadFromJsonAsync<BillCloseProof>(cancellationToken: ct))!;
        }
    }
    public void Dispose() { Cashier?.Dispose(); OrderCashier?.Dispose(); Payments?.Dispose(); Kitchen?.Dispose(); Ordering?.Dispose(); }
}
