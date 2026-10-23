using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;
using AICOS.CustomerEngagement.Api;
using AICOS.CustomerEngagement.Api.Persistence;

namespace AICOS.Services.IntegrationTests;

/// <summary>UC-03 on the Customer side: declared drink preferences, and the profile copy kept in sync with Identity.</summary>
public sealed class CustomerProfileTests : IDisposable
{
    private const string Preferences = "/api/v1/customers/me/preferences";

    private readonly ServiceFactory<CustomerEngagementService, CustomerEngagementDbContext> _f = new();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _tenant = ServiceFactory<CustomerEngagementService, CustomerEngagementDbContext>.DefaultTenantId;
    private readonly HttpClient _me;

    public CustomerProfileTests() => _me = _f.As(AicosRoles.Customer, userId: _userId);

    public void Dispose() => _f.Dispose();

    private Task RegisterAsync() => _f.HandleAsync(new CustomerRegisteredEvent(_userId, "Nguyễn An", "an@example.com", "0901234567", _tenant));

    [Fact]
    public async Task A_new_customer_has_empty_preferences_and_can_save_them()
    {
        await RegisterAsync();

        var empty = await _me.GetFromJsonAsync<JsonElement>(Preferences);
        empty.GetProperty("sugarPercent").ValueKind.Should().Be(JsonValueKind.Null);
        empty.GetProperty("favoriteToppings").GetArrayLength().Should().Be(0);

        await _me.Put(Preferences, new
        {
            sugarPercent = 50, ice = "less", favoriteToppings = new[] { "Trân châu", " Thạch cà phê " }, notes = "Dị ứng đậu phộng",
        }).ShouldBe(200);

        var saved = await _me.GetFromJsonAsync<JsonElement>(Preferences);
        saved.GetProperty("sugarPercent").GetInt32().Should().Be(50);
        saved.GetProperty("ice").GetString().Should().Be("Less"); // stored canonically
        saved.GetProperty("favoriteToppings").EnumerateArray().Select(t => t.GetString())
            .Should().Equal("Trân châu", "Thạch cà phê");
        saved.GetProperty("notes").GetString().Should().Be("Dị ứng đậu phộng");
    }

    [Fact]
    public async Task Saving_preferences_replaces_the_previous_set()
    {
        await RegisterAsync();
        await _me.Put(Preferences, new { sugarPercent = 70, ice = "Normal", favoriteToppings = new[] { "A", "B" } }).ShouldBe(200);

        await _me.Put(Preferences, new { sugarPercent = 0 }).ShouldBe(200);

        var saved = await _me.GetFromJsonAsync<JsonElement>(Preferences);
        saved.GetProperty("sugarPercent").GetInt32().Should().Be(0);
        saved.GetProperty("ice").ValueKind.Should().Be(JsonValueKind.Null);
        saved.GetProperty("favoriteToppings").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Preferences_are_validated()
    {
        await RegisterAsync();

        await _me.Put(Preferences, new { sugarPercent = 150 }).ShouldBe(400);
        await _me.Put(Preferences, new { sugarPercent = -1 }).ShouldBe(400);
        await _me.Put(Preferences, new { ice = "Extra" }).ShouldBe(400);
        await _me.Put(Preferences, new { favoriteToppings = Enumerable.Range(0, 11).Select(i => $"T{i}").ToArray() }).ShouldBe(400);
        await _me.Put(Preferences, new { favoriteToppings = new[] { "Kem", "kem" } }).ShouldBe(400);
        await _me.Put(Preferences, new { favoriteToppings = new[] { new string('x', 51) } }).ShouldBe(400);
        await _me.Put(Preferences, new { notes = new string('n', 501) }).ShouldBe(400);
    }

    [Fact]
    public async Task Preferences_are_private_to_the_signed_in_customer()
    {
        await RegisterAsync();
        await _me.Put(Preferences, new { sugarPercent = 30 }).ShouldBe(200);

        await _f.As(AicosRoles.Customer).GetAsync(Preferences).ShouldBe(404);          // another account, no profile
        await _f.CreateClient().GetAsync(Preferences).ShouldBe(401);                   // anonymous
    }

    [Fact]
    public async Task Profile_updated_event_syncs_name_and_phone_but_keeps_other_fields()
    {
        await RegisterAsync();
        await _me.Put("/api/v1/customers/me", new { fullName = "An Nguyễn", birthday = "2000-05-20", marketingOptIn = true }).ShouldBe(200);

        await _f.HandleAsync(new UserProfileUpdatedEvent(_userId, "An Văn Nguyễn", "0988888888"));
        await _f.HandleAsync(new UserProfileUpdatedEvent(_userId, "An Văn Nguyễn", "0988888888")); // redelivery is harmless

        var profile = await _me.GetFromJsonAsync<JsonElement>("/api/v1/customers/me");
        profile.GetProperty("fullName").GetString().Should().Be("An Văn Nguyễn");
        profile.GetProperty("phone").GetString().Should().Be("0988888888");
        profile.GetProperty("birthday").GetString().Should().Be("2000-05-20");
        profile.GetProperty("marketingOptIn").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Profile_updated_event_for_an_unknown_user_is_ignored()
    {
        await _f.HandleAsync(new UserProfileUpdatedEvent(Guid.NewGuid(), "Nobody", null)); // must not throw
    }
}
