using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Security.Cryptography;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;
using AICOS.Payments.Api;
using AICOS.Payments.Api.Infrastructure;
using AICOS.Payments.Api.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AICOS.Services.IntegrationTests;

/// <summary>Shared set-up for the Payment service tests: one brand with two branches, the Mock provider on, and clients per role.</summary>
public abstract class PaymentTestBase : IDisposable
{
    protected const string Tx = "/api/v1/payments/transactions";
    protected const string Refunds = "/api/v1/payments/refunds";
    protected const string Policy = "/api/v1/payments/policies/cancellation";
    protected const string Hooks = "/api/v1/payments/webhooks";
    protected const string Providers = "/api/v1/payments/providers";
    protected const string Merchants = "/api/v1/payments/merchant-settings";
    protected const string MockSecret = "aicos-mock-webhook-secret";

    protected static readonly string TestKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    protected readonly FakeOrderDirectory Orders = new();
    protected readonly ServiceFactory<PaymentsService, PaymentsDbContext> F;
    protected readonly Guid Tenant = ServiceFactory<PaymentsService, PaymentsDbContext>.DefaultTenantId;
    protected readonly Guid OtherTenant = Guid.NewGuid();
    protected readonly Guid B1;
    protected readonly Guid B2;
    protected readonly Guid ForeignBranch;
    protected readonly Guid CashierId = Guid.NewGuid();
    protected readonly Guid ManagerId = Guid.NewGuid();
    protected readonly Guid CustomerId = Guid.NewGuid();
    protected readonly HttpClient Super;
    protected readonly HttpClient Admin;
    protected readonly HttpClient Manager1;
    protected readonly HttpClient Cashier1;
    protected readonly HttpClient Cashier2;
    protected readonly HttpClient Customer;
    protected readonly HttpClient OtherAdmin;

    protected PaymentTestBase(Action<IServiceCollection>? configure = null)
    {
        F = new ServiceFactory<PaymentsService, PaymentsDbContext>(services =>
        {
            services.Configure<PaymentOptions>(o => o.SecretKey = TestKey);
            services.RemoveAll<AICOS.Payments.Api.Infrastructure.IOrderDirectory>();
            services.AddSingleton<AICOS.Payments.Api.Infrastructure.IOrderDirectory>(Orders);
            configure?.Invoke(services);
        });
        F.Observer = RegisterOrderAsync;
        B1 = F.Branches.Add(Tenant, name: "Quận 1");
        B2 = F.Branches.Add(Tenant, name: "Quận 3");
        ForeignBranch = F.Branches.Add(OtherTenant, name: "Foreign");
        Super = F.As(AicosRoles.SuperAdmin);
        Admin = F.As(AicosRoles.BrandAdmin);
        OtherAdmin = F.As(AicosRoles.BrandAdmin, tenantId: OtherTenant);
        Manager1 = F.As(AicosRoles.BranchManager, B1, ManagerId);
        Cashier1 = F.As(AicosRoles.Cashier, B1, CashierId);
        Cashier2 = F.As(AicosRoles.Cashier, B2);
        Customer = F.As(AicosRoles.Customer, userId: CustomerId);

        Super.PostOk(Providers, new { code = "Mock", displayName = "Mock", mode = "Mock" }).GetAwaiter().GetResult();
        SetProviderEnabledAsync("Mock", true).GetAwaiter().GetResult();
    }

    public void Dispose() => F.Dispose();

    /// <summary>Makes the order a payment request names exist, with the amount and branch of the request, unless the test registered it already.</summary>
    private async Task RegisterOrderAsync(HttpRequestMessage request)
    {
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;
        if (request.Method != HttpMethod.Post || request.Content is null || !(path.EndsWith("/transactions") || path.EndsWith("/transactions/cash")))
        {
            return;
        }

        try
        {
            var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync()).RootElement;
            if (!body.TryGetProperty("orderId", out var id) || !body.TryGetProperty("amount", out var amount) || id.ValueKind != JsonValueKind.String || amount.ValueKind != JsonValueKind.Number)
            {
                return;
            }

            var branch = body.TryGetProperty("branchId", out var b) && b.ValueKind == JsonValueKind.String ? b.GetGuid() : BranchOfToken(request);
            if (!Orders.Knows(id.GetGuid()) && branch is not null && amount.GetDecimal() > 0 && amount.GetDecimal() == decimal.Truncate(amount.GetDecimal()))
            {
                Orders.Set(id.GetGuid(), branch.Value, amount.GetDecimal());
            }
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            // a malformed request: let the endpoint answer it
        }
    }

    private static Guid? BranchOfToken(HttpRequestMessage request)
    {
        var token = request.Headers.Authorization?.Parameter;
        var parts = token?.Split('.');
        if (parts is not { Length: 3 })
        {
            return null;
        }

        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        var claims = JsonDocument.Parse(Convert.FromBase64String(payload)).RootElement;
        return claims.TryGetProperty("branch_id", out var branch) && Guid.TryParse(branch.GetString(), out var id) ? id : null;
    }

    protected async Task SetProviderEnabledAsync(string code, bool enabled)
    {
        var list = await Super.GetFromJsonAsync<JsonElement>(Providers);
        var id = list.EnumerateArray().First(p => p.GetProperty("code").GetString() == code).Id();
        await Super.Patch($"{Providers}/{id}/status", new { isEnabled = enabled }).ShouldBe(200);
    }

    protected async Task EnableVietQrAsync()
    {
        await Super.PostOk(Providers, new { code = "VietQR", displayName = "VietQR", mode = "Sandbox", merchantId = "P", apiKey = "ak_1234567890", secretKey = "sk_1234567890" });
        await SetProviderEnabledAsync("VietQR", true);
        await Admin.Put(Merchants, new { providerCode = "VietQR", isEnabled = true, accountName = "CONG TY AICOS", bankCode = "vcb", accountNumber = "0123456789012" }).ShouldBe(201);
    }

    /// <summary>The customer starts an online payment at branch 1.</summary>
    protected async Task<JsonElement> PayOnlineAsync(Guid? orderId = null, decimal amount = 100_000, string provider = "Mock", HttpClient? client = null) =>
        await (client ?? Customer).PostOk(Tx, new { orderId = orderId ?? Guid.NewGuid(), branchId = B1, amount, providerCode = provider });

    protected async Task<JsonElement> SimulateAsync(Guid paymentId, string outcome = "success", bool notify = true, HttpClient? client = null) =>
        await (client ?? Customer).PostOk($"{Tx}/{paymentId}/simulate", new { outcome, notify });

    /// <summary>An online payment that was paid.</summary>
    protected async Task<JsonElement> PaidOnlineAsync(decimal amount = 100_000, Guid? orderId = null)
    {
        var payment = await PayOnlineAsync(orderId, amount);
        return await SimulateAsync(payment.Id());
    }

    protected async Task<JsonElement> PaidCashAsync(decimal amount = 100_000, Guid? orderId = null) =>
        await Cashier1.PostOk($"{Tx}/cash", new { orderId = orderId ?? Guid.NewGuid(), amount }).ContinueWith(t => t.Result.GetProperty("payment"));

    protected async Task<JsonElement> GetAsync(HttpClient client, string url) => await client.GetFromJsonAsync<JsonElement>(url);

    protected static string Sign(string eventId, string transactionRef, string status, decimal amount, string secret = MockSecret) =>
        WebhookSignature.Compute(secret, eventId, transactionRef, status, amount);

    protected Task<HttpResponseMessage> WebhookAsync(string provider, string eventId, string transactionRef, string status, decimal amount, string? signature = null, string secret = MockSecret) =>
        F.CreateClient().Post($"{Hooks}/{provider}", new { eventId, transactionRef, status, amount, signature = signature ?? Sign(eventId, transactionRef, status, amount, secret) });
}

/// <summary>SCRUM-85/86/87: paying an order through a provider or in cash, webhooks, status query, expiry.</summary>
public sealed class PaymentFlowTests : PaymentTestBase
{
    [Fact]
    public async Task A_customer_pays_online_with_the_mock_provider_and_the_order_service_is_told()
    {
        var order = Guid.NewGuid();
        var created = await Customer.Post(Tx, new { orderId = order, branchId = B1, amount = 85_000, providerCode = "mock" });

        created.StatusCode.Should().Be(System.Net.HttpStatusCode.Created);
        var payment = await created.Body();
        payment.GetProperty("status").GetString().Should().Be("Pending");
        payment.GetProperty("method").GetString().Should().Be("Mock");
        payment.GetProperty("checkoutUrl").GetString().Should().Contain(payment.GetProperty("transactionRef").GetString()!);
        payment.GetProperty("expiresAtUtc").GetDateTime().Should().Be(F.Clock.Now.UtcDateTime.AddMinutes(15));   // default policy: unpaid orders are released after 15 minutes

        var paid = await SimulateAsync(payment.Id());

        paid.GetProperty("status").GetString().Should().Be("Succeeded");
        paid.GetProperty("refundable").GetDecimal().Should().Be(85_000);
        var ev = F.Events.Published.OfType<PaymentSucceededEvent>().Single();
        (ev.TenantId, ev.BranchId, ev.OrderId, ev.Amount, ev.Method).Should().Be((Tenant, B1, order, 85_000m, "Mock"));
        await Customer.Post(Tx, new { orderId = order, branchId = B1, amount = 85_000, providerCode = "Mock" }).ShouldBe(409, "payment.already_paid");
    }

    [Fact]
    public async Task Pressing_pay_twice_returns_the_same_payment_and_another_method_replaces_the_old_attempt()
    {
        var order = Guid.NewGuid();
        var first = await PayOnlineAsync(order, 50_000);

        var again = await Customer.Post(Tx, new { orderId = order, branchId = B1, amount = 50_000, providerCode = "Mock" });
        again.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        (await again.Body()).Id().Should().Be(first.Id());

        await EnableVietQrAsync();
        var changed = await PayOnlineAsync(order, 50_000, provider: "VietQR");
        changed.Id().Should().NotBe(first.Id());
        (await GetAsync(Customer, $"{Tx}/{first.Id()}")).GetProperty("status").GetString().Should().Be("Cancelled");
    }

    [Fact]
    public async Task Input_is_validated_and_unavailable_methods_are_refused()
    {
        var order = Guid.NewGuid();
        await Customer.Post(Tx, new { }).ShouldBe(400);
        await Customer.Post(Tx, new { orderId = order, branchId = B1, amount = 0, providerCode = "Mock" }).ShouldBe(400);
        await Customer.Post(Tx, new { orderId = order, branchId = B1, amount = 1000.5, providerCode = "Mock" }).ShouldBe(400);
        await Customer.Post(Tx, new { orderId = order, branchId = B1, amount = 1000, providerCode = "Cash" }).ShouldBe(400);
        await Customer.Post(Tx, new { orderId = order, branchId = B1, amount = 1000, providerCode = "Paypal" }).ShouldBe(400);
        await Customer.Post(Tx, new { orderId = order, amount = 1000, providerCode = "Mock" }).ShouldBe(400);                      // a customer names the branch
        await Customer.Post(Tx, new { orderId = order, branchId = ForeignBranch, amount = 1000, providerCode = "Mock" }).ShouldBe(400);   // another brand's branch
        await Customer.Post(Tx, new { orderId = order, branchId = B1, amount = 1000, providerCode = "VietQR" }).ShouldBe(422, "payment.method_unavailable");

        await SetProviderEnabledAsync("Mock", false);                                                                                 // the platform switches it off
        await Customer.Post(Tx, new { orderId = order, branchId = B1, amount = 1000, providerCode = "Mock" }).ShouldBe(422, "payment.method_unavailable");
        await F.CreateClient().Post(Tx, new { orderId = order, branchId = B1, amount = 1000, providerCode = "Mock" }).ShouldBe(401);
    }

    [Fact]
    public async Task A_branch_that_switched_a_method_off_cannot_take_it()
    {
        await Admin.Put(Merchants, new { providerCode = "Mock", isEnabled = false, branchId = B2 }).ShouldBe(201);

        await Customer.Post(Tx, new { orderId = Guid.NewGuid(), branchId = B2, amount = 1000, providerCode = "Mock" }).ShouldBe(422, "payment.method_unavailable");
        await Customer.Post(Tx, new { orderId = Guid.NewGuid(), branchId = B1, amount = 1000, providerCode = "Mock" }).ShouldBe(201);
    }

    [Fact]
    public async Task VietQR_builds_the_transfer_qr_from_the_receiving_account_of_the_brand()
    {
        await EnableVietQrAsync();

        var payment = await PayOnlineAsync(amount: 120_000, provider: "VietQR");

        var url = payment.GetProperty("checkoutUrl").GetString()!;
        url.Should().StartWith("https://img.vietqr.io/image/VCB-0123456789012-").And.Contain("amount=120000").And.Contain(payment.GetProperty("transactionRef").GetString()!);
    }

    [Fact]
    public async Task Momo_and_ZaloPay_payments_are_started_with_their_own_checkout_links()
    {
        foreach (var code in new[] { "Momo", "ZaloPay" })
        {
            await Super.PostOk(Providers, new { code, displayName = code, mode = "Sandbox", merchantId = "P", apiKey = "ak_1234567890", secretKey = "sk_1234567890" });
            await SetProviderEnabledAsync(code, true);
            await Admin.Put(Merchants, new { providerCode = code, isEnabled = true, merchantCode = "M-1" }).ShouldBe(201);

            var payment = await PayOnlineAsync(provider: code);

            payment.GetProperty("checkoutUrl").GetString().Should().Contain($"/{code.ToLowerInvariant()}/");
        }
    }

    [Fact]
    public async Task A_signed_webhook_marks_the_payment_paid_and_a_retry_changes_nothing()
    {
        var payment = await PayOnlineAsync(amount: 70_000);
        var txRef = payment.GetProperty("transactionRef").GetString()!;

        var first = await WebhookAsync("Mock", "evt-1", txRef, "success", 70_000);
        var retry = await WebhookAsync("Mock", "evt-1", txRef, "success", 70_000);

        (await first.Body()).GetProperty("outcome").GetString().Should().Be("applied");
        var again = await retry.Body();
        again.GetProperty("duplicate").GetBoolean().Should().BeTrue();
        again.GetProperty("outcome").GetString().Should().Be("applied");
        F.Events.Published.OfType<PaymentSucceededEvent>().Should().ContainSingle();
        (await GetAsync(Customer, $"{Tx}/{payment.Id()}")).GetProperty("status").GetString().Should().Be("Succeeded");

        var other = await WebhookAsync("Mock", "evt-2", txRef, "success", 70_000);                         // a different event for a paid payment
        (await other.Body()).GetProperty("outcome").GetString().Should().Be("ignored_final");
        F.Events.Published.OfType<PaymentSucceededEvent>().Should().ContainSingle();
    }

    [Fact]
    public async Task A_webhook_with_a_wrong_or_missing_signature_is_rejected_and_changes_nothing()
    {
        var payment = await PayOnlineAsync(amount: 70_000);
        var txRef = payment.GetProperty("transactionRef").GetString()!;

        await WebhookAsync("Mock", "e1", txRef, "success", 70_000, signature: "deadbeef").ShouldBe(401, "webhook.invalid_signature");
        await WebhookAsync("Mock", "e2", txRef, "success", 70_000, secret: "some-other-secret").ShouldBe(401, "webhook.invalid_signature");
        await WebhookAsync("Mock", "e3", txRef, "success", 70_000, signature: "").ShouldBe(401);
        await WebhookAsync("Mock", "e4", txRef, "success", 99_999, signature: Sign("e4", txRef, "success", 70_000)).ShouldBe(401);   // amount tampered
        await WebhookAsync("VietQR", "e5", txRef, "success", 70_000).ShouldBe(401);                                                  // VietQR has no key configured here
        await WebhookAsync("Paypal", "e6", txRef, "success", 70_000).ShouldBe(404);
        await F.CreateClient().Post($"{Hooks}/Mock", new { eventId = "e7", transactionRef = txRef, status = "maybe", amount = 1, signature = "x" }).ShouldBe(400);

        (await GetAsync(Customer, $"{Tx}/{payment.Id()}")).GetProperty("status").GetString().Should().Be("Pending");
        F.Events.Published.OfType<PaymentSucceededEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task A_real_provider_signs_its_webhooks_with_its_own_secret_key()
    {
        await EnableVietQrAsync();
        var payment = await PayOnlineAsync(amount: 90_000, provider: "VietQR");
        var txRef = payment.GetProperty("transactionRef").GetString()!;

        await WebhookAsync("VietQR", "v1", txRef, "success", 90_000, secret: MockSecret).ShouldBe(401);
        var ok = await WebhookAsync("VietQR", "v1", txRef, "success", 90_000, secret: "sk_1234567890");

        (await ok.Body()).GetProperty("outcome").GetString().Should().Be("applied");
        (await GetAsync(Customer, $"{Tx}/{payment.Id()}")).GetProperty("status").GetString().Should().Be("Succeeded");
    }

    [Fact]
    public async Task A_webhook_for_the_wrong_amount_is_not_applied_and_an_unknown_payment_is_only_recorded()
    {
        var payment = await PayOnlineAsync(amount: 70_000);
        var txRef = payment.GetProperty("transactionRef").GetString()!;

        var wrong = await WebhookAsync("Mock", "m1", txRef, "success", 10_000);
        var unknown = await WebhookAsync("Mock", "m2", "AICOSNOSUCHPAYMENT0", "success", 70_000);

        (await wrong.Body()).GetProperty("outcome").GetString().Should().Be("amount_mismatch");
        (await unknown.Body()).GetProperty("outcome").GetString().Should().Be("unknown_payment");
        (await GetAsync(Customer, $"{Tx}/{payment.Id()}")).GetProperty("status").GetString().Should().Be("Pending");
        F.Events.Published.OfType<PaymentSucceededEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_webhook_fails_the_payment_and_releases_the_order()
    {
        var order = Guid.NewGuid();
        var payment = await PayOnlineAsync(order, 70_000);

        await WebhookAsync("Mock", "f1", payment.GetProperty("transactionRef").GetString()!, "failed", 70_000).ShouldBe(200);

        var read = await GetAsync(Customer, $"{Tx}/{payment.Id()}");
        read.GetProperty("status").GetString().Should().Be("Failed");
        var failed = F.Events.Published.OfType<PaymentFailedEvent>().Single();
        (failed.OrderId, failed.BranchId, failed.Method).Should().Be((order, B1, "Mock"));
        (await PayOnlineAsync(order, 70_000)).Id().Should().NotBe(payment.Id());                              // the customer may try again
    }

    [Fact]
    public async Task The_status_query_finds_a_payment_whose_webhook_was_lost()
    {
        var payment = await PayOnlineAsync(amount: 70_000);
        (await SimulateAsync(payment.Id(), notify: false)).GetProperty("status").GetString().Should().Be("Pending");   // paid at the provider, we never heard
        F.Events.Published.OfType<PaymentSucceededEvent>().Should().BeEmpty();

        var queried = await Customer.PostOk($"{Tx}/{payment.Id()}/query", new { });

        queried.GetProperty("status").GetString().Should().Be("Succeeded");
        F.Events.Published.OfType<PaymentSucceededEvent>().Should().ContainSingle();
        (await Customer.PostOk($"{Tx}/{payment.Id()}/query", new { })).GetProperty("status").GetString().Should().Be("Succeeded");   // asking again is harmless
        F.Events.Published.OfType<PaymentSucceededEvent>().Should().ContainSingle();
    }

    [Fact]
    public async Task The_status_query_leaves_an_unpaid_payment_pending_and_records_a_decline()
    {
        var pending = await PayOnlineAsync(amount: 70_000);
        (await Customer.PostOk($"{Tx}/{pending.Id()}/query", new { })).GetProperty("status").GetString().Should().Be("Pending");

        await SimulateAsync(pending.Id(), "failure", notify: false);

        (await Customer.PostOk($"{Tx}/{pending.Id()}/query", new { })).GetProperty("status").GetString().Should().Be("Failed");
    }

    [Fact]
    public async Task An_unpaid_payment_expires_after_the_time_of_the_brand_policy_and_money_that_arrives_later_still_counts()
    {
        await Admin.Put(Policy, new { cancelRefundPercent = 100, noShowAfterMinutes = 30, noShowRefundPercent = 0, unpaidOrderTimeoutMinutes = 10, autoApproveWithinPolicy = true, exceptionApprover = "BranchManager" }).ShouldBe(200);
        var payment = await PayOnlineAsync(amount: 70_000);
        payment.GetProperty("expiresAtUtc").GetDateTime().Should().Be(F.Clock.Now.UtcDateTime.AddMinutes(10));

        F.Clock.Now = F.Clock.Now.AddMinutes(11);
        var expired = await GetAsync(Customer, $"{Tx}/{payment.Id()}");

        expired.GetProperty("status").GetString().Should().Be("Expired");
        F.Events.Published.OfType<PaymentFailedEvent>().Should().ContainSingle();

        var late = await WebhookAsync("Mock", "late-1", payment.GetProperty("transactionRef").GetString()!, "success", 70_000);

        (await late.Body()).GetProperty("outcome").GetString().Should().Be("applied_late");
        var paid = await GetAsync(Customer, $"{Tx}/{payment.Id()}");
        paid.GetProperty("status").GetString().Should().Be("Succeeded");
        paid.GetProperty("paidLate").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task The_expiry_job_expires_every_overdue_payment_without_anyone_asking()
    {
        var a = await PayOnlineAsync(amount: 10_000);
        var b = await PayOnlineAsync(amount: 20_000);
        var fresh = await PayOnlineAsync(amount: 30_000);
        F.Clock.Now = F.Clock.Now.AddMinutes(16);
        var late = await PayOnlineAsync(amount: 40_000);                                                      // started after the clock moved

        using (var scope = F.Services.CreateScope())
        {
            (await scope.ServiceProvider.GetRequiredService<PaymentProcessor>().ExpireDueAsync(CancellationToken.None)).Should().Be(3);
        }

        foreach (var id in new[] { a.Id(), b.Id(), fresh.Id() })
        {
            (await GetAsync(Customer, $"{Tx}/{id}")).GetProperty("status").GetString().Should().Be("Expired");
        }

        (await GetAsync(Customer, $"{Tx}/{late.Id()}")).GetProperty("status").GetString().Should().Be("Pending");
        F.Events.Published.OfType<PaymentFailedEvent>().Should().HaveCount(3);
    }

    [Fact]
    public async Task A_pending_payment_can_be_cancelled_once()
    {
        var payment = await PayOnlineAsync();

        (await Customer.PostOk($"{Tx}/{payment.Id()}/cancel", new { })).GetProperty("status").GetString().Should().Be("Cancelled");

        await Customer.Post($"{Tx}/{payment.Id()}/cancel", new { }).ShouldBe(422, "payment.not_pending");
        F.Events.Published.OfType<PaymentFailedEvent>().Should().ContainSingle();
        var paid = await PaidOnlineAsync();
        await Customer.Post($"{Tx}/{paid.Id()}/cancel", new { }).ShouldBe(422, "payment.not_pending");
    }

    [Fact]
    public async Task Simulation_can_be_switched_off_for_production()
    {
        using var locked = new PaymentFlowLocked();
        var payment = await locked.PayAsync();

        await locked.CustomerClient.Post($"{Tx}/{payment}/simulate", new { outcome = "success" }).ShouldBe(403, "payment.simulation_disabled");
    }

    [Fact]
    public async Task Simulating_needs_a_valid_outcome_and_cannot_be_used_on_cash_or_paid_payments()
    {
        var payment = await PayOnlineAsync();
        await Customer.Post($"{Tx}/{payment.Id()}/simulate", new { outcome = "maybe" }).ShouldBe(400);
        var cash = await PaidCashAsync();
        await Cashier1.Post($"{Tx}/{cash.Id()}/simulate", new { outcome = "success" }).ShouldBe(422, "payment.cash_not_simulated");
        var paid = await SimulateAsync(payment.Id());
        await Customer.Post($"{Tx}/{paid.Id()}/simulate", new { outcome = "success" }).ShouldBe(422, "payment.already_paid");
    }

    // ------------------------------------------------------------------ cash
    [Fact]
    public async Task The_cashier_records_cash_and_the_cash_closing_is_told()
    {
        var order = Guid.NewGuid();

        var response = await Cashier1.Post($"{Tx}/cash", new { orderId = order, amount = 95_000, amountTendered = 100_000 });

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Created);
        var body = await response.Body();
        body.GetProperty("change").GetDecimal().Should().Be(5_000);
        var payment = body.GetProperty("payment");
        (payment.GetProperty("status").GetString(), payment.GetProperty("method").GetString()).Should().Be(("Succeeded", "Cash"));
        var cash = F.Events.Published.OfType<CashPaymentRecordedEvent>().Single();
        (cash.TenantId, cash.BranchId, cash.CashierUserId, cash.OrderId, cash.Amount).Should().Be((Tenant, B1, CashierId, order, 95_000m));
        F.Events.Published.OfType<PaymentSucceededEvent>().Single().Method.Should().Be("Cash");

        await Cashier1.Post($"{Tx}/cash", new { orderId = order, amount = 95_000 }).ShouldBe(409, "payment.already_paid");
        await Customer.Post($"{Tx}", new { orderId = order, branchId = B1, amount = 95_000, providerCode = "Mock" }).ShouldBe(409, "payment.already_paid");
    }

    [Fact]
    public async Task Paying_cash_cancels_an_online_attempt_for_the_same_order()
    {
        var order = Guid.NewGuid();
        var online = await PayOnlineAsync(order, 50_000);

        await Cashier1.PostOk($"{Tx}/cash", new { orderId = order, amount = 50_000 });

        (await GetAsync(Cashier1, $"{Tx}/{online.Id()}")).GetProperty("status").GetString().Should().Be("Cancelled");
    }

    [Fact]
    public async Task Cash_is_validated_and_only_counter_staff_can_take_it_at_their_own_branch()
    {
        var order = Guid.NewGuid();
        await Cashier1.Post($"{Tx}/cash", new { }).ShouldBe(400);
        await Cashier1.Post($"{Tx}/cash", new { orderId = order, amount = -5 }).ShouldBe(400);
        await Cashier1.Post($"{Tx}/cash", new { orderId = order, amount = 50_000, amountTendered = 40_000 }).ShouldBe(400);
        await Cashier1.Post($"{Tx}/cash", new { orderId = order, amount = 50_000, branchId = B2 }).ShouldBe(403, "branch.forbidden");

        await Customer.Post($"{Tx}/cash", new { orderId = order, amount = 50_000 }).ShouldBe(403);
        await F.As(AicosRoles.Barista, B1).Post($"{Tx}/cash", new { orderId = order, amount = 50_000 }).ShouldBe(403);
        await Admin.Post($"{Tx}/cash", new { orderId = order, amount = 50_000 }).ShouldBe(400);                                  // a brand admin names the branch
        await Admin.Post($"{Tx}/cash", new { orderId = order, amount = 50_000, branchId = ForeignBranch }).ShouldBe(400);
        await Admin.Post($"{Tx}/cash", new { orderId = Guid.NewGuid(), amount = 50_000, branchId = B2 }).ShouldBe(201);

        F.Branches.Suspend(B1);
        await Cashier1.Post($"{Tx}/cash", new { orderId = Guid.NewGuid(), amount = 50_000 }).ShouldBe(201);                      // staff of a suspended branch are not checked against Identity
    }

    // ------------------------------------------------------------------ access
    [Fact]
    public async Task Payments_are_visible_only_to_their_owner_their_branch_and_their_brand()
    {
        var mine = await PayOnlineAsync();
        var strangers = F.As(AicosRoles.Customer);

        await strangers.GetAsync($"{Tx}/{mine.Id()}").ShouldBe(404);
        await Customer.GetAsync($"{Tx}/{mine.Id()}").ShouldBe(200);
        await Cashier1.GetAsync($"{Tx}/{mine.Id()}").ShouldBe(200);
        await Manager1.GetAsync($"{Tx}/{mine.Id()}").ShouldBe(200);
        await Admin.GetAsync($"{Tx}/{mine.Id()}").ShouldBe(200);
        await Cashier2.GetAsync($"{Tx}/{mine.Id()}").ShouldBe(403, "branch.forbidden");
        await OtherAdmin.GetAsync($"{Tx}/{mine.Id()}").ShouldBe(404);
        await Super.GetAsync($"{Tx}/{mine.Id()}").ShouldBe(403, "tenant.required");
        await F.CreateClient().GetAsync($"{Tx}/{mine.Id()}").ShouldBe(401);
        await strangers.Post($"{Tx}/{mine.Id()}/cancel", new { }).ShouldBe(404);
    }

    [Fact]
    public async Task Lists_show_a_customer_their_own_payments_and_staff_their_branch()
    {
        var order = Guid.NewGuid();
        await PayOnlineAsync(order);
        await PayOnlineAsync(client: F.As(AicosRoles.Customer));
        await PaidCashAsync();
        await Cashier2.PostOk($"{Tx}/cash", new { orderId = Guid.NewGuid(), amount = 1_000 });

        (await GetAsync(Customer, Tx)).GetArrayLength().Should().Be(1);
        (await GetAsync(Cashier1, Tx)).GetArrayLength().Should().Be(3);                                    // branch 1: two online, one cash
        (await GetAsync(Cashier2, Tx)).GetArrayLength().Should().Be(1);
        (await GetAsync(Admin, Tx)).GetArrayLength().Should().Be(4);
        (await GetAsync(Admin, $"{Tx}?branchId={B2}")).GetArrayLength().Should().Be(1);
        (await GetAsync(Cashier1, $"{Tx}?orderId={order}")).GetArrayLength().Should().Be(1);
        (await GetAsync(Admin, $"{Tx}?status=Succeeded")).GetArrayLength().Should().Be(2);
        (await GetAsync(OtherAdmin, Tx)).GetArrayLength().Should().Be(0);
        await Cashier1.GetAsync($"{Tx}?branchId={B2}").ShouldBe(403, "branch.forbidden");
    }
}

/// <summary>A second Payment service with simulation turned off.</summary>
internal sealed class PaymentFlowLocked : PaymentTestBase
{
    public PaymentFlowLocked()
        : base(services => services.Configure<PaymentOptions>(o => o.AllowSimulation = false))
    {
    }

    public HttpClient CustomerClient => Customer;

    public async Task<Guid> PayAsync() => (await PayOnlineAsync()).Id();
}
