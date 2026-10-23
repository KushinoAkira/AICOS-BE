using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;
using AICOS.IdentityOrganization.Api.Features.Auth;
using AICOS.IdentityOrganization.Api.Features.Branches;
using AICOS.IdentityOrganization.Api.Features.Users;

namespace AICOS.IdentityOrganization.Tests;

/// <summary>UC-03: every role can edit their own profile and change their own password.</summary>
public sealed class ProfileTests : IDisposable
{
    private const string Auth = "/api/v1/identity/auth";
    private const string Password = "Temp1234";
    private static readonly JsonSerializerOptions Json = AuthEndpointsTests.Json;

    private readonly IdentityApiFactory _factory = new();
    private readonly HttpClient _admin;

    public ProfileTests() => _admin = _factory.CreateClientAs(AicosRoles.BrandAdmin);

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task A_staff_member_edits_name_phone_and_username_but_not_email_or_role()
    {
        var branch = await CreateBranchAsync();
        await CreateUserAsync("bar@example.com", AicosRoles.Barista, branch.Id);
        using var me = await SignedInAsync("bar@example.com");

        var response = await me.PutAsJsonAsync($"{Auth}/me", new UpdateProfileRequest("Lê Barista", "0912345678", "bar.le"), Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = (await response.Content.ReadFromJsonAsync<UserResponse>(Json))!;
        updated.FullName.Should().Be("Lê Barista");
        updated.Phone.Should().Be("0912345678");
        updated.Username.Should().Be("bar.le");
        updated.Email.Should().Be("bar@example.com");
        updated.Role.Should().Be(AicosRoles.Barista);
        updated.BranchId.Should().Be(branch.Id);

        (await me.GetFromJsonAsync<UserResponse>($"{Auth}/me", Json))!.FullName.Should().Be("Lê Barista");
    }

    [Theory]
    [InlineData(AicosRoles.BrandAdmin)]
    [InlineData(AicosRoles.BranchManager)]
    [InlineData(AicosRoles.Barista)]
    [InlineData(AicosRoles.Cashier)]
    [InlineData(AicosRoles.Customer)]
    public async Task Every_role_can_edit_their_profile_and_change_their_password(string role)
    {
        var branch = await CreateBranchAsync();
        var email = $"{role.ToLowerInvariant()}@example.com";
        await CreateUserAsync(email, role, role is AicosRoles.BrandAdmin or AicosRoles.Customer ? null : branch.Id);
        using var me = await SignedInAsync(email);

        (await me.PutAsJsonAsync($"{Auth}/me", new UpdateProfileRequest("Tên Mới", null, null), Json))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await me.PostAsJsonAsync($"{Auth}/change-password", new ChangePasswordRequest(Password, "Brand1234", "Brand1234"), Json))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await _factory.CreateClient().PostAsJsonAsync($"{Auth}/login", new LoginRequest(email, "Brand1234"), Json))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Profile_fields_are_validated()
    {
        await CreateUserAsync("c@example.com", AicosRoles.Customer, null);
        using var me = await SignedInAsync("c@example.com");

        var response = await me.PutAsJsonAsync($"{Auth}/me", new UpdateProfileRequest("", "12ab", "1bad name"), Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        foreach (var field in new[] { "FullName", "Phone", "Username" })
        {
            errors.TryGetProperty(field, out _).Should().BeTrue($"{field} should be reported");
        }
    }

    [Fact]
    public async Task A_username_already_taken_by_someone_else_is_rejected_but_your_own_is_fine()
    {
        var branch = await CreateBranchAsync();
        await CreateUserAsync("one@example.com", AicosRoles.Cashier, branch.Id, username: "taken.name");
        await CreateUserAsync("two@example.com", AicosRoles.Cashier, branch.Id, username: "two.name");
        using var two = await SignedInAsync("two@example.com");

        (await two.PutAsJsonAsync($"{Auth}/me", new UpdateProfileRequest("Two", null, "Taken.Name"), Json))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await two.PutAsJsonAsync($"{Auth}/me", new UpdateProfileRequest("Two", null, "two.name"), Json))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Clearing_the_username_and_phone_is_allowed()
    {
        var branch = await CreateBranchAsync();
        await CreateUserAsync("one@example.com", AicosRoles.Cashier, branch.Id, username: "one.name");
        using var me = await SignedInAsync("one@example.com");

        var updated = await (await me.PutAsJsonAsync($"{Auth}/me", new UpdateProfileRequest("One", null, null), Json))
            .Content.ReadFromJsonAsync<UserResponse>(Json);

        updated!.Username.Should().BeNull();
        updated.Phone.Should().BeNull();
    }

    [Fact]
    public async Task Only_a_customer_edit_is_published_so_Customer_Engagement_can_sync_it()
    {
        var branch = await CreateBranchAsync();
        await CreateUserAsync("cashier@example.com", AicosRoles.Cashier, branch.Id);
        await CreateUserAsync("cus@example.com", AicosRoles.Customer, null);
        using var cashier = await SignedInAsync("cashier@example.com");
        using var customer = await SignedInAsync("cus@example.com");

        await cashier.PutAsJsonAsync($"{Auth}/me", new UpdateProfileRequest("Cashier New", "0911111111", null), Json);
        _factory.Events.Published.OfType<UserProfileUpdatedEvent>().Should().BeEmpty();

        var response = await customer.PutAsJsonAsync($"{Auth}/me", new UpdateProfileRequest("Khách Mới", "0922222222", null), Json);
        var user = (await response.Content.ReadFromJsonAsync<UserResponse>(Json))!;

        _factory.Events.Published.OfType<UserProfileUpdatedEvent>().Should()
            .ContainSingle(e => e.UserId == user.Id && e.FullName == "Khách Mới" && e.Phone == "0922222222");
    }

    [Fact]
    public async Task Editing_the_profile_requires_a_signed_in_account()
    {
        (await _factory.CreateClient().PutAsJsonAsync($"{Auth}/me", new UpdateProfileRequest("X", null, null), Json))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // A token with no account behind it (like /dev/token) gets 404 rather than creating anything.
        using var orphan = _factory.CreateClientAs(AicosRoles.Cashier, Guid.NewGuid());
        (await orphan.PutAsJsonAsync($"{Auth}/me", new UpdateProfileRequest("X", null, null), Json))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ------------------------------------------------------------------ helpers
    private async Task<BranchResponse> CreateBranchAsync()
    {
        var response = await _admin.PostAsJsonAsync("/api/v1/identity/branches", new BranchRequest($"Branch {Guid.NewGuid():N}", null, null, null, null), Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<BranchResponse>(Json))!;
    }

    private async Task CreateUserAsync(string email, string role, Guid? branchId, string? username = null)
    {
        var response = await _admin.PostAsJsonAsync("/api/v1/identity/users",
            new CreateUserRequest("Test User", email, username, null, Password, role, branchId), Json);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Logs in for real and returns a client carrying that account's own access token.</summary>
    private async Task<HttpClient> SignedInAsync(string email)
    {
        var login = await _factory.CreateClient().PostAsJsonAsync($"{Auth}/login", new LoginRequest(email, Password), Json);
        login.EnsureSuccessStatusCode();
        var auth = (await login.Content.ReadFromJsonAsync<AuthResponse>(Json))!;

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return client;
    }
}
