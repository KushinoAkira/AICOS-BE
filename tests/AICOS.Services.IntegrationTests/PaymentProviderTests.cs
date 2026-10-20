using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;
using AICOS.Payments.Api;
using AICOS.Payments.Api.Domain;
using AICOS.Payments.Api.Infrastructure;
using AICOS.Payments.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AICOS.Services.IntegrationTests;

/// <summary>UC-08 / FR-06: the platform-wide payment provider configuration, with credentials encrypted at rest.</summary>
public sealed class PaymentProviderTests : IDisposable
{
    private const string Url = "/api/v1/payments/providers";
    private const string ApiKey = "ak_live_1234567890abcdef";
    private const string SecretKey = "sk_super_secret_value_9876";

    private static readonly string TestKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private readonly ServiceFactory<PaymentsService, PaymentsDbContext> _f =
        new(services => services.Configure<PaymentOptions>(o => o.SecretKey = TestKey));

    private readonly HttpClient _superAdmin;

    public PaymentProviderTests() => _superAdmin = _f.As(AicosRoles.SuperAdmin);

    public void Dispose() => _f.Dispose();

    private static object VietQr(string? apiKey = ApiKey, string? secret = SecretKey) => new
    {
        code = "VietQR", displayName = "VietQR", mode = "Sandbox", merchantId = "MERCHANT-01", apiKey, secretKey = secret,
        callbackUrl = "https://api.example.com/api/v1/payments/webhooks/vietqr",
    };

    // ------------------------------------------------------------------ configure
    [Fact]
    public async Task A_sandbox_provider_is_created_without_ever_returning_its_credentials()
    {
        var response = await _superAdmin.Post(Url, VietQr());
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Created);

        var text = await response.Content.ReadAsStringAsync();
        text.Should().NotContain(ApiKey).And.NotContain(SecretKey);
        var body = JsonDocument.Parse(text).RootElement;
        body.GetProperty("code").GetString().Should().Be("VietQR");
        body.GetProperty("isEnabled").GetBoolean().Should().BeFalse(); // not live until the Super Admin enables it
        body.GetProperty("merchantId").GetString().Should().Be("MERCHANT-01");
        body.GetProperty("hasApiKey").GetBoolean().Should().BeTrue();
        body.GetProperty("hasSecretKey").GetBoolean().Should().BeTrue();
        body.GetProperty("apiKeyHint").GetString().Should().Be("••••cdef"); // last 4 only

        var again = await _superAdmin.GetStringAsync($"{Url}/{body.Id()}");
        again.Should().NotContain(ApiKey).And.NotContain(SecretKey);
        (await _superAdmin.GetStringAsync(Url)).Should().NotContain(ApiKey).And.NotContain(SecretKey);
    }

    [Fact]
    public async Task Credentials_are_encrypted_in_the_database_and_can_be_read_back_with_the_key()
    {
        var created = await _superAdmin.PostOk(Url, VietQr());

        using var scope = _f.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().PaymentProviders.FirstAsync(p => p.Id == created.Id());
        stored.ApiKeyEncrypted.Should().NotBeNull().And.NotContain(ApiKey);
        stored.SecretKeyEncrypted.Should().NotBeNull().And.NotContain(SecretKey);

        var protector = scope.ServiceProvider.GetRequiredService<SecretProtector>();
        protector.Unprotect(stored.ApiKeyEncrypted!).Should().Be(ApiKey);
        protector.Unprotect(stored.SecretKeyEncrypted!).Should().Be(SecretKey);
    }

    [Fact]
    public async Task A_real_provider_cannot_be_enabled_until_all_its_credentials_are_set()
    {
        var created = await _superAdmin.PostOk(Url, VietQr(apiKey: null, secret: null));

        await _superAdmin.Patch($"{Url}/{created.Id()}/status", new { isEnabled = true }).ShouldBe(422, "provider.credentials_missing");

        await _superAdmin.Put($"{Url}/{created.Id()}", new
        {
            displayName = "VietQR", mode = "Sandbox", merchantId = "MERCHANT-01", apiKey = ApiKey, secretKey = SecretKey,
        }).ShouldBe(200);
        var enabled = await _superAdmin.Patch($"{Url}/{created.Id()}/status", new { isEnabled = true });
        (await enabled.Body()).GetProperty("isEnabled").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task The_mock_provider_needs_no_credentials_and_takes_none()
    {
        var mock = await _superAdmin.PostOk(Url, new { code = "mock", displayName = "Mock payment", mode = "Mock" });
        mock.GetProperty("code").GetString().Should().Be("Mock"); // canonical spelling

        var enabled = await _superAdmin.Patch($"{Url}/{mock.Id()}/status", new { isEnabled = true });
        (await enabled.Body()).GetProperty("isEnabled").GetBoolean().Should().BeTrue();

        await _superAdmin.Put($"{Url}/{mock.Id()}", new { displayName = "Mock", mode = "Mock", apiKey = "x" }).ShouldBe(400);
        await _superAdmin.Put($"{Url}/{mock.Id()}", new { displayName = "Mock", mode = "Sandbox" }).ShouldBe(400);
    }

    [Fact]
    public async Task Updating_keeps_stored_keys_unless_a_new_value_is_sent()
    {
        var created = await _superAdmin.PostOk(Url, VietQr());
        await _superAdmin.Patch($"{Url}/{created.Id()}/status", new { isEnabled = true }).ShouldBe(200);

        await _superAdmin.Put($"{Url}/{created.Id()}", new { displayName = "VietQR (renamed)", mode = "Sandbox", merchantId = "MERCHANT-02" }).ShouldBe(200);
        var kept = await _superAdmin.GetFromJsonAsync<JsonElement>($"{Url}/{created.Id()}");
        kept.GetProperty("displayName").GetString().Should().Be("VietQR (renamed)");
        kept.GetProperty("merchantId").GetString().Should().Be("MERCHANT-02");
        kept.GetProperty("apiKeyHint").GetString().Should().Be("••••cdef");
        kept.GetProperty("isEnabled").GetBoolean().Should().BeTrue();

        await _superAdmin.Put($"{Url}/{created.Id()}", new { displayName = "VietQR", mode = "Sandbox", merchantId = "MERCHANT-02", apiKey = "ak_rotated_00000000zzzz" }).ShouldBe(200);
        (await _superAdmin.GetFromJsonAsync<JsonElement>($"{Url}/{created.Id()}")).GetProperty("apiKeyHint").GetString().Should().Be("••••zzzz");
    }

    [Fact]
    public async Task Provider_input_is_validated()
    {
        await _superAdmin.Post(Url, new { code = "Paypal", displayName = "X", mode = "Sandbox" }).ShouldBe(400);                   // unsupported
        await _superAdmin.Post(Url, new { code = "Momo", displayName = "", mode = "Sandbox" }).ShouldBe(400);                      // name required
        await _superAdmin.Post(Url, new { code = "Momo", displayName = "Momo", mode = "Mock" }).ShouldBe(400);                     // real provider can't be mock
        await _superAdmin.Post(Url, new { code = "Momo", displayName = "Momo", mode = "Sandbox", callbackUrl = "ftp://x" }).ShouldBe(400);
        await _superAdmin.Post(Url, new { code = "Momo", displayName = "Momo", mode = "Sandbox", callbackUrl = "not a url" }).ShouldBe(400);
        await _superAdmin.Post(Url, new { code = "Momo", displayName = "Momo", mode = "Sandbox", apiKey = new string('k', 501) }).ShouldBe(400);
        await _superAdmin.Post(Url, new { code = "Momo", displayName = new string('n', 101), mode = "Sandbox" }).ShouldBe(400);
    }

    [Fact]
    public async Task Each_provider_can_only_be_configured_once()
    {
        await _superAdmin.PostOk(Url, VietQr());

        await _superAdmin.Post(Url, VietQr()).ShouldBe(409, "provider.duplicate_code");
    }

    [Fact]
    public async Task Unknown_providers_are_404_and_status_needs_a_value()
    {
        await _superAdmin.GetAsync($"{Url}/{Guid.NewGuid()}").ShouldBe(404);
        var created = await _superAdmin.PostOk(Url, new { code = "ZaloPay", displayName = "ZaloPay", mode = "Sandbox" });
        await _superAdmin.Patch($"{Url}/{created.Id()}/status", new { }).ShouldBe(400);
    }

    [Fact]
    public async Task The_list_can_be_filtered_by_enabled()
    {
        var mock = await _superAdmin.PostOk(Url, new { code = "Mock", displayName = "Mock", mode = "Mock" });
        await _superAdmin.PostOk(Url, new { code = "ZaloPay", displayName = "ZaloPay", mode = "Sandbox" });
        await _superAdmin.Patch($"{Url}/{mock.Id()}/status", new { isEnabled = true }).ShouldBe(200);

        (await _superAdmin.GetFromJsonAsync<JsonElement>($"{Url}?isEnabled=true")).GetArrayLength().Should().Be(1);
        (await _superAdmin.GetFromJsonAsync<JsonElement>(Url)).GetArrayLength().Should().Be(2);
    }

    // ------------------------------------------------------------------ access
    [Theory]
    [InlineData(AicosRoles.BrandAdmin)]
    [InlineData(AicosRoles.BranchManager)]
    [InlineData(AicosRoles.Cashier)]
    [InlineData(AicosRoles.Customer)]
    public async Task Only_the_super_admin_can_read_or_change_the_configuration(string role)
    {
        var created = await _superAdmin.PostOk(Url, VietQr());
        var client = _f.As(role, Guid.NewGuid());

        await client.GetAsync(Url).ShouldBe(403);
        await client.GetAsync($"{Url}/{created.Id()}").ShouldBe(403);
        await client.Post(Url, VietQr()).ShouldBe(403);
        await client.Put($"{Url}/{created.Id()}", new { displayName = "x", mode = "Sandbox" }).ShouldBe(403);
        await client.Patch($"{Url}/{created.Id()}/status", new { isEnabled = true }).ShouldBe(403);
    }

    [Fact]
    public async Task Anonymous_callers_get_401()
    {
        await _f.CreateClient().GetAsync(Url).ShouldBe(401);
        await _f.CreateClient().GetAsync($"{Url}/enabled").ShouldBe(401);
    }

    [Theory]
    [InlineData(AicosRoles.BrandAdmin)]
    [InlineData(AicosRoles.Cashier)]
    [InlineData(AicosRoles.Customer)]
    public async Task Any_signed_in_user_sees_the_enabled_providers_without_secrets(string role)
    {
        var mock = await _superAdmin.PostOk(Url, new { code = "Mock", displayName = "Mock payment", mode = "Mock" });
        await _superAdmin.Patch($"{Url}/{mock.Id()}/status", new { isEnabled = true }).ShouldBe(200);
        var vietQr = await _superAdmin.PostOk(Url, VietQr());                                  // configured but not enabled

        var text = await _f.As(role, Guid.NewGuid()).GetStringAsync($"{Url}/enabled");

        var list = JsonDocument.Parse(text).RootElement;
        list.GetArrayLength().Should().Be(1);
        list[0].GetProperty("code").GetString().Should().Be("Mock");
        list[0].EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("code", "displayName", "mode");
        text.Should().NotContain(vietQr.Id().ToString()).And.NotContain("MERCHANT");
    }

    // ------------------------------------------------------------------ audit (FR-67)
    [Fact]
    public async Task Every_change_is_sent_to_the_audit_history_without_any_secret()
    {
        var created = await _superAdmin.PostOk(Url, VietQr());
        await _superAdmin.Put($"{Url}/{created.Id()}", new { displayName = "VietQR 2", mode = "Sandbox", merchantId = "MERCHANT-01" }).ShouldBe(200);
        await _superAdmin.Patch($"{Url}/{created.Id()}/status", new { isEnabled = true }).ShouldBe(200);

        var events = _f.Events.Published.OfType<AuditRecordedEvent>().ToList();

        events.Select(e => e.Action).Should().Equal("provider.created", "provider.updated", "provider.status_changed");
        events.Should().OnlyContain(e => e.TenantId == null && e.EntityType == "PaymentProvider" && e.EntityId == created.Id().ToString() && e.ActorRole == AicosRoles.SuperAdmin);
        var all = string.Join(' ', events.Select(e => e.Before + e.After));
        all.Should().NotContain(ApiKey).And.NotContain(SecretKey).And.NotContain("ak_").And.NotContain("sk_");
        events[0].After.Should().Contain("\"hasApiKey\":true");
        events[2].Before.Should().Contain("\"isEnabled\":false");
        events[2].After.Should().Contain("\"isEnabled\":true");
    }

    [Fact]
    public async Task A_failed_change_publishes_no_audit_event()
    {
        await _superAdmin.Post(Url, new { code = "Paypal", displayName = "X", mode = "Sandbox" }).ShouldBe(400);

        _f.Events.Published.OfType<AuditRecordedEvent>().Should().BeEmpty();
    }

    // ------------------------------------------------------------------ secret protector
    [Fact]
    public void The_protector_round_trips_and_uses_a_fresh_nonce_each_time()
    {
        var protector = new SecretProtector(Options.Create(new PaymentOptions { SecretKey = TestKey }));

        var one = protector.Protect("hello");
        var two = protector.Protect("hello");

        one.Should().NotBe(two).And.NotContain("hello");
        protector.Unprotect(one).Should().Be("hello");
        protector.Unprotect(two).Should().Be("hello");
        protector.Unprotect(protector.Protect("Tiếng Việt: khóa bí mật")).Should().Be("Tiếng Việt: khóa bí mật");
    }

    [Fact]
    public void A_tampered_value_or_a_different_key_cannot_be_decrypted()
    {
        var protector = new SecretProtector(Options.Create(new PaymentOptions { SecretKey = TestKey }));
        var cipher = Convert.FromBase64String(protector.Protect("secret"));
        cipher[^1] ^= 0xFF;

        FluentActions.Invoking(() => protector.Unprotect(Convert.ToBase64String(cipher))).Should().Throw<CryptographicException>();

        var other = new SecretProtector(Options.Create(new PaymentOptions { SecretKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }));
        FluentActions.Invoking(() => other.Unprotect(protector.Protect("secret"))).Should().Throw<CryptographicException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-base64!!")]
    [InlineData("c2hvcnQ=")] // valid base64 but only 5 bytes
    public void A_missing_or_malformed_key_fails_loudly_instead_of_storing_plain_text(string? key)
    {
        var protector = new SecretProtector(Options.Create(new PaymentOptions { SecretKey = key }));

        FluentActions.Invoking(() => protector.Protect("secret")).Should().Throw<InvalidOperationException>().WithMessage("*Payments:SecretKey*");
    }
}
