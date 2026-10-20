using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;
using AICOS.Payments.Api;
using AICOS.Payments.Api.Infrastructure;
using AICOS.Payments.Api.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AICOS.Services.IntegrationTests;

/// <summary>UC-18 the brand cancellation and refund policy, UC-20 the brand and branch merchant payment settings.</summary>
public sealed class PaymentConfigTests : IDisposable
{
    private const string Policy = "/api/v1/payments/policies/cancellation";
    private const string Merchants = "/api/v1/payments/merchant-settings";
    private const string Providers = "/api/v1/payments/providers";
    private const string AccountNumber = "0123456789012";

    private static readonly string TestKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private readonly ServiceFactory<PaymentsService, PaymentsDbContext> _f =
        new(services => services.Configure<PaymentOptions>(o => o.SecretKey = TestKey));

    private readonly Guid _tenant = ServiceFactory<PaymentsService, PaymentsDbContext>.DefaultTenantId;
    private readonly Guid _otherTenant = Guid.NewGuid();
    private readonly Guid _b1;
    private readonly Guid _b2;
    private readonly Guid _otherBranch;
    private readonly HttpClient _super;
    private readonly HttpClient _admin;
    private readonly HttpClient _otherAdmin;
    private readonly HttpClient _manager;

    public PaymentConfigTests()
    {
        _b1 = _f.Branches.Add(_tenant, name: "Quận 1");
        _b2 = _f.Branches.Add(_tenant, name: "Quận 3");
        _otherBranch = _f.Branches.Add(_otherTenant, name: "Foreign");
        _super = _f.As(AicosRoles.SuperAdmin);
        _admin = _f.As(AicosRoles.BrandAdmin);
        _otherAdmin = _f.As(AicosRoles.BrandAdmin, tenantId: _otherTenant);
        _manager = _f.As(AicosRoles.BranchManager, _b1);
    }

    public void Dispose() => _f.Dispose();

    private static object NewPolicy(int cancelRefund = 100, int noShowAfter = 30, int noShowRefund = 0, int unpaidTimeout = 15, bool auto = true, string approver = "BranchManager") => new
    {
        cancelRefundPercent = cancelRefund, noShowAfterMinutes = noShowAfter, noShowRefundPercent = noShowRefund,
        unpaidOrderTimeoutMinutes = unpaidTimeout, autoApproveWithinPolicy = auto, exceptionApprover = approver,
    };

    // ------------------------------------------------------------------ cancellation policy (UC-18)
    [Fact]
    public async Task A_brand_that_never_configured_the_policy_gets_the_platform_defaults()
    {
        var policy = await _admin.GetFromJsonAsync<JsonElement>(Policy);

        policy.GetProperty("isDefault").GetBoolean().Should().BeTrue();
        policy.GetProperty("cancelRefundPercent").GetInt32().Should().Be(100);
        policy.GetProperty("noShowAfterMinutes").GetInt32().Should().Be(30);          // BR-58 default
        policy.GetProperty("noShowRefundPercent").GetInt32().Should().Be(0);
        policy.GetProperty("unpaidOrderTimeoutMinutes").GetInt32().Should().Be(15);   // BR-25 default
        policy.GetProperty("autoApproveWithinPolicy").GetBoolean().Should().BeTrue(); // BR-75
        policy.GetProperty("exceptionApprover").GetString().Should().Be("BranchManager");
    }

    [Fact]
    public async Task The_brand_admin_sets_the_policy_and_every_signed_in_user_of_the_brand_can_read_it()
    {
        var saved = await _admin.Put(Policy, NewPolicy(cancelRefund: 80, noShowAfter: 45, noShowRefund: 20, unpaidTimeout: 10, auto: false, approver: "BrandAdmin"));
        saved.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        (await saved.Body()).GetProperty("isDefault").GetBoolean().Should().BeFalse();

        foreach (var role in new[] { AicosRoles.BrandAdmin, AicosRoles.BranchManager, AicosRoles.Cashier, AicosRoles.Customer })
        {
            var read = await _f.As(role, role == AicosRoles.Customer ? null : _b1).GetFromJsonAsync<JsonElement>(Policy);
            read.GetProperty("cancelRefundPercent").GetInt32().Should().Be(80);
            read.GetProperty("exceptionApprover").GetString().Should().Be("BrandAdmin");
            read.GetProperty("autoApproveWithinPolicy").GetBoolean().Should().BeFalse();
        }

        await _admin.Put(Policy, NewPolicy(cancelRefund: 50)).ShouldBe(200);                                      // a second save replaces the first
        (await _admin.GetFromJsonAsync<JsonElement>(Policy)).GetProperty("cancelRefundPercent").GetInt32().Should().Be(50);
        (await _otherAdmin.GetFromJsonAsync<JsonElement>(Policy)).GetProperty("isDefault").GetBoolean().Should().BeTrue();   // another brand is unaffected
    }

    [Fact]
    public async Task The_policy_is_validated_and_only_the_brand_admin_writes_it()
    {
        await _admin.Put(Policy, new { }).ShouldBe(400);
        await _admin.Put(Policy, NewPolicy(cancelRefund: 101)).ShouldBe(400);
        await _admin.Put(Policy, NewPolicy(cancelRefund: -1)).ShouldBe(400);
        await _admin.Put(Policy, NewPolicy(noShowRefund: 101)).ShouldBe(400);
        await _admin.Put(Policy, NewPolicy(noShowAfter: 4)).ShouldBe(400);
        await _admin.Put(Policy, NewPolicy(noShowAfter: 241)).ShouldBe(400);
        await _admin.Put(Policy, NewPolicy(unpaidTimeout: 4)).ShouldBe(400);
        await _admin.Put(Policy, NewPolicy(unpaidTimeout: 121)).ShouldBe(400);
        await _admin.Put(Policy, NewPolicy(approver: "Cashier")).ShouldBe(400);

        await _manager.Put(Policy, NewPolicy()).ShouldBe(403);
        await _f.As(AicosRoles.Customer).Put(Policy, NewPolicy()).ShouldBe(403);
        await _super.Put(Policy, NewPolicy()).ShouldBe(403);
        await _f.CreateClient().GetAsync(Policy).ShouldBe(401);
        await _super.GetAsync(Policy).ShouldBe(403, "tenant.required");
    }

    [Fact]
    public async Task Policy_changes_are_sent_to_the_audit_history_with_before_and_after()
    {
        await _admin.Put(Policy, NewPolicy(cancelRefund: 90)).ShouldBe(200);

        var audit = _f.Events.Published.OfType<AuditRecordedEvent>().Single();
        audit.Action.Should().Be("cancellation_policy.updated");
        audit.TenantId.Should().Be(_tenant);
        audit.ActorRole.Should().Be(AicosRoles.BrandAdmin);
        audit.Before.Should().Contain("\"isDefault\":true").And.Contain("\"cancelRefundPercent\":100");
        audit.After.Should().Contain("\"cancelRefundPercent\":90").And.Contain("\"isDefault\":false");
    }

    // ------------------------------------------------------------------ merchant settings (UC-20)
    private async Task<JsonElement> EnableProviderAsync(string code, string mode = "Sandbox")
    {
        var provider = await _super.PostOk(Providers, code == "Mock"
            ? new { code, displayName = code, mode = "Mock" }
            : new { code, displayName = code, mode, merchantId = "PLATFORM", apiKey = "ak_1234567890", secretKey = "sk_1234567890" });
        await _super.Patch($"{Providers}/{provider.Id()}/status", new { isEnabled = true }).ShouldBe(200);
        return provider;
    }

    private static object Setting(string provider, bool enabled = true, Guid? branchId = null, string? number = AccountNumber) => new
    {
        branchId, providerCode = provider, isEnabled = enabled, accountName = "CONG TY AICOS", bankCode = "vcb", accountNumber = number,
    };

    [Fact]
    public async Task The_brand_admin_sets_up_a_receiving_account_that_is_never_returned_in_full()
    {
        await EnableProviderAsync("VietQR");

        var response = await _admin.Put(Merchants, Setting("vietqr"));

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Created);
        var text = await response.Content.ReadAsStringAsync();
        text.Should().NotContain(AccountNumber);
        var body = JsonDocument.Parse(text).RootElement;
        body.GetProperty("providerCode").GetString().Should().Be("VietQR");
        body.GetProperty("bankCode").GetString().Should().Be("VCB");                       // normalised
        body.GetProperty("hasAccountNumber").GetBoolean().Should().BeTrue();
        body.GetProperty("accountNumberHint").GetString().Should().Be("••••9012");
        body.GetProperty("branchId").ValueKind.Should().Be(JsonValueKind.Null);
        (await _admin.GetStringAsync(Merchants)).Should().NotContain(AccountNumber);
    }

    [Fact]
    public async Task The_account_number_is_encrypted_in_the_database_and_kept_when_an_update_omits_it()
    {
        await EnableProviderAsync("VietQR");
        var created = await _admin.PostPutOk(Merchants, Setting("VietQR"));

        using (var scope = _f.Services.CreateScope())
        {
            var stored = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().MerchantSettings.Single();
            stored.AccountNumberEncrypted.Should().NotBeNull().And.NotContain(AccountNumber);
            scope.ServiceProvider.GetRequiredService<SecretProtector>().Unprotect(stored.AccountNumberEncrypted!).Should().Be(AccountNumber);
        }

        var updated = await _admin.Put(Merchants, new { providerCode = "VietQR", isEnabled = true, accountName = "TÊN MỚI", bankCode = "ACB" });
        updated.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);                      // an update, not a second row
        var body = await updated.Body();
        body.Id().Should().Be(created.Id());
        body.GetProperty("accountName").GetString().Should().Be("TÊN MỚI");
        body.GetProperty("accountNumberHint").GetString().Should().Be("••••9012");
        (await _admin.GetFromJsonAsync<JsonElement>(Merchants)).GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task A_provider_can_only_be_switched_on_when_the_platform_enabled_it_and_real_providers_need_details()
    {
        await _admin.Put(Merchants, Setting("VietQR")).ShouldBe(422, "merchant.provider_unavailable");        // the platform never configured it
        var provider = await _super.PostOk(Providers, new { code = "Momo", displayName = "Momo", mode = "Sandbox", merchantId = "P", apiKey = "ak_1234567890", secretKey = "sk_1234567890" });
        await _admin.Put(Merchants, new { providerCode = "Momo", isEnabled = true, merchantCode = "M1" }).ShouldBe(422, "merchant.provider_disabled");
        await _admin.Put(Merchants, new { providerCode = "Momo", isEnabled = false }).ShouldBe(201);                   // switching off is always possible

        await _super.Patch($"{Providers}/{provider.Id()}/status", new { isEnabled = true }).ShouldBe(200);
        await _admin.Put(Merchants, new { providerCode = "Momo", isEnabled = true }).ShouldBe(422, "merchant.details_missing");
        await _admin.Put(Merchants, new { providerCode = "Momo", isEnabled = true, merchantCode = "MOMO-001" }).ShouldBe(200);
    }

    [Fact]
    public async Task Merchant_input_is_validated()
    {
        await EnableProviderAsync("VietQR");
        await EnableProviderAsync("Mock");

        await _admin.Put(Merchants, new { isEnabled = true }).ShouldBe(400);                                                    // provider required
        await _admin.Put(Merchants, new { providerCode = "Paypal", isEnabled = true }).ShouldBe(400);
        await _admin.Put(Merchants, new { providerCode = "VietQR" }).ShouldBe(400);                                              // isEnabled required
        await _admin.Put(Merchants, Setting("VietQR", number: "12345")).ShouldBe(400);                                          // too short
        await _admin.Put(Merchants, Setting("VietQR", number: "1234 5678 90")).ShouldBe(400);                                    // spaces
        await _admin.Put(Merchants, new { providerCode = "VietQR", isEnabled = true, accountName = new string('n', 101), accountNumber = AccountNumber }).ShouldBe(400);
        await _admin.Put(Merchants, new { providerCode = "Mock", isEnabled = true, accountName = "x" }).ShouldBe(400);          // the mock takes no details
        await _admin.Put(Merchants, new { providerCode = "Mock", isEnabled = true }).ShouldBe(201);
    }

    [Fact]
    public async Task A_branch_setting_overrides_the_brand_setting_only_for_that_branch()
    {
        await EnableProviderAsync("VietQR");
        await _admin.Put(Merchants, Setting("VietQR")).ShouldBe(201);                                    // brand-wide: enabled
        await _admin.Put(Merchants, Setting("VietQR", enabled: false, branchId: _b2)).ShouldBe(201);      // branch 2: switched off

        string[] CodesAt(JsonElement methods) => methods.EnumerateArray().Select(m => m.GetProperty("providerCode").GetString()!).ToArray();
        var atB1 = await _f.As(AicosRoles.Cashier, _b1).GetFromJsonAsync<JsonElement>($"{Merchants}/available");
        var atB2 = await _f.As(AicosRoles.Cashier, _b2).GetFromJsonAsync<JsonElement>($"{Merchants}/available");
        var forCustomer = await _f.As(AicosRoles.Customer).GetFromJsonAsync<JsonElement>($"{Merchants}/available?branchId={_b2}");

        CodesAt(atB1).Should().Equal("VietQR");
        atB1[0].GetProperty("source").GetString().Should().Be("Brand");
        CodesAt(atB2).Should().BeEmpty();
        CodesAt(forCustomer).Should().BeEmpty();
        (await _f.As(AicosRoles.Customer).GetFromJsonAsync<JsonElement>($"{Merchants}/available?branchId={_b1}"))[0].GetProperty("providerCode").GetString().Should().Be("VietQR");

        await _admin.Put(Merchants, Setting("VietQR", enabled: true, branchId: _b2)).ShouldBe(200);        // switching the branch back on
        (await _f.As(AicosRoles.Cashier, _b2).GetFromJsonAsync<JsonElement>($"{Merchants}/available"))[0].GetProperty("source").GetString().Should().Be("Branch");
    }

    [Fact]
    public async Task Only_providers_the_platform_enabled_are_offered_and_the_mock_needs_no_setup()
    {
        await EnableProviderAsync("Mock");
        var vietQr = await EnableProviderAsync("VietQR");
        await _admin.Put(Merchants, Setting("VietQR")).ShouldBe(201);

        (await _f.As(AicosRoles.Cashier, _b1).GetFromJsonAsync<JsonElement>($"{Merchants}/available")).EnumerateArray()
            .Select(m => m.GetProperty("providerCode").GetString()).Should().Equal("Mock", "VietQR");

        await _super.Patch($"{Providers}/{vietQr.Id()}/status", new { isEnabled = false }).ShouldBe(200);      // the platform switches it off for everybody
        (await _f.As(AicosRoles.Cashier, _b1).GetFromJsonAsync<JsonElement>($"{Merchants}/available")).EnumerateArray()
            .Select(m => m.GetProperty("providerCode").GetString()).Should().Equal("Mock");
    }

    [Fact]
    public async Task Branches_of_another_brand_are_refused_and_settings_are_isolated_per_brand_BR04()
    {
        await EnableProviderAsync("VietQR");
        var mine = await _admin.PostPutOk(Merchants, Setting("VietQR"));

        await _admin.Put(Merchants, Setting("VietQR", branchId: _otherBranch)).ShouldBe(400);
        await _admin.Put(Merchants, Setting("VietQR", branchId: Guid.NewGuid())).ShouldBe(400);
        (await _otherAdmin.GetFromJsonAsync<JsonElement>(Merchants)).GetArrayLength().Should().Be(0);
        await _otherAdmin.DeleteAsync($"{Merchants}/{mine.Id()}").ShouldBe(404);
        await _otherAdmin.PostPutOk(Merchants, Setting("VietQR", number: "99999999999"));                                  // their own, independent row
        (await _admin.GetFromJsonAsync<JsonElement>(Merchants)).GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task A_branch_manager_reads_the_settings_of_their_branch_but_cannot_change_them()
    {
        await EnableProviderAsync("VietQR");
        await _admin.Put(Merchants, Setting("VietQR")).ShouldBe(201);
        await _admin.Put(Merchants, Setting("VietQR", branchId: _b1)).ShouldBe(201);
        await _admin.Put(Merchants, Setting("VietQR", branchId: _b2)).ShouldBe(201);

        (await _admin.GetFromJsonAsync<JsonElement>(Merchants)).GetArrayLength().Should().Be(3);
        (await _admin.GetFromJsonAsync<JsonElement>($"{Merchants}?branchId={_b2}")).GetArrayLength().Should().Be(1);
        var seenByManager = await _manager.GetFromJsonAsync<JsonElement>(Merchants);
        seenByManager.GetArrayLength().Should().Be(2);                                                                         // brand-wide + own branch
        seenByManager.EnumerateArray().Select(s => s.TryGetProperty("branchId", out var b) && b.ValueKind == JsonValueKind.String ? b.GetGuid() : (Guid?)null)
            .Should().BeEquivalentTo(new Guid?[] { null, _b1 });

        await _manager.Put(Merchants, Setting("VietQR")).ShouldBe(403);
        await _f.As(AicosRoles.Cashier, _b1).GetAsync(Merchants).ShouldBe(403);
        await _f.As(AicosRoles.Customer).GetAsync(Merchants).ShouldBe(403);
        await _super.GetAsync(Merchants).ShouldBe(403);
        await _f.CreateClient().GetAsync($"{Merchants}/available").ShouldBe(401);
    }

    [Fact]
    public async Task A_setting_can_be_removed_and_every_change_is_audited_without_the_account_number()
    {
        await EnableProviderAsync("VietQR");
        var created = await _admin.PostPutOk(Merchants, Setting("VietQR"));
        await _admin.Put(Merchants, new { providerCode = "VietQR", isEnabled = false }).ShouldBe(200);
        await _admin.DeleteAsync($"{Merchants}/{created.Id()}").ShouldBe(204);
        await _admin.DeleteAsync($"{Merchants}/{created.Id()}").ShouldBe(404);

        var events = _f.Events.Published.OfType<AuditRecordedEvent>().Where(e => e.EntityType == "MerchantSetting").ToList();

        events.Select(e => e.Action).Should().Equal("merchant_setting.created", "merchant_setting.updated", "merchant_setting.deleted");
        events.Should().OnlyContain(e => e.TenantId == _tenant && e.EntityId == created.Id().ToString());
        string.Join(' ', events.Select(e => e.Before + e.After)).Should().NotContain(AccountNumber);
        events[0].After.Should().Contain("\"hasAccountNumber\":true").And.Contain("\"accountNumberHint\":\"9012\"");
        events[1].Before.Should().Contain("\"isEnabled\":true");
        events[1].After.Should().Contain("\"isEnabled\":false");
    }
}

internal static class PutExtensions
{
    /// <summary>PUT that must succeed (200 or 201); returns the parsed body.</summary>
    public static async Task<JsonElement> PostPutOk(this HttpClient client, string url, object body)
    {
        var response = await client.Put(url, body);
        var text = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue($"PUT {url} returned {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }
}
