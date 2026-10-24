using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;
using AICOS.IdentityOrganization.Api.Domain;
using AICOS.IdentityOrganization.Api.Features.Auth;
using AICOS.IdentityOrganization.Api.Features.Branches;
using AICOS.BuildingBlocks.Persistence;
using AICOS.IdentityOrganization.Api.Features.Users;

namespace AICOS.IdentityOrganization.Tests;

public sealed class UserEndpointsTests : IDisposable
{
    private const string Url = "/api/v1/identity/users";
    private const string Password = "Temp1234";
    private static readonly JsonSerializerOptions Json = AuthEndpointsTests.Json;

    private readonly IdentityApiFactory _factory = new();
    private readonly Guid _adminId = Guid.NewGuid();
    private readonly HttpClient _admin;

    public UserEndpointsTests() => _admin = _factory.CreateClientAs(AicosRoles.BrandAdmin, userId: _adminId);

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Admin_creates_staff_with_branch_and_temporary_password()
    {
        var branch = await CreateBranchAsync();

        var response = await PostAsync(NewUser("cashier@example.com", "cashier", AicosRoles.Cashier, branch.Id));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var user = (await response.Content.ReadFromJsonAsync<UserResponse>(Json))!;
        user.BranchId.Should().Be(branch.Id);
        user.MustChangePassword.Should().BeTrue();
        user.Status.Should().Be(UserStatus.Active);
        response.Headers.Location!.ToString().Should().EndWith($"/users/{user.Id}");
    }

    [Fact]
    public async Task Role_is_case_insensitive_and_stored_canonically()
    {
        var response = await PostAsync(NewUser("a2@example.com", null, "brandadmin", null));

        (await response.Content.ReadFromJsonAsync<UserResponse>(Json))!.Role.Should().Be(AicosRoles.BrandAdmin);
    }

    [Fact]
    public async Task Creating_a_customer_publishes_CustomerRegisteredEvent()
    {
        await PostAsync(NewUser("cus@example.com", null, AicosRoles.Customer, null));

        _factory.Events.Published.OfType<CustomerRegisteredEvent>().Should().ContainSingle(e => e.Email == "cus@example.com");
    }

    [Theory]
    [InlineData("BranchManager", false, "BranchId")]        // staff role without a branch
    [InlineData("BrandAdmin", true, "BranchId")]     // admin must not have a branch
    [InlineData("Customer", true, "BranchId")]        // customer must not have a branch
    [InlineData("Guest", false, "Role")]              // not assignable
    [InlineData("Owner", false, "Role")]              // unknown
    public async Task Role_and_branch_must_be_consistent(string role, bool withBranch, string errorField)
    {
        var branchId = withBranch ? (await CreateBranchAsync()).Id : (Guid?)null;

        var response = await PostAsync(NewUser("x@example.com", null, role, branchId));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorsAsync(response)).TryGetProperty(errorField, out _).Should().BeTrue();
    }

    [Fact]
    public async Task Unknown_or_suspended_branch_is_rejected()
    {
        (await PostAsync(NewUser("y@example.com", null, AicosRoles.Cashier, Guid.NewGuid())))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var branch = await CreateBranchAsync();
        await _admin.PatchAsJsonAsync($"/api/v1/identity/branches/{branch.Id}/status", new { status = "Suspended" });
        (await PostAsync(NewUser("z@example.com", null, AicosRoles.Cashier, branch.Id)))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Validation_reports_every_bad_field()
    {
        var response = await PostAsync(new CreateUserRequest(" ", "bad", "1x", "phone", "weak", "Cashier", null));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var errors = await ErrorsAsync(response);
        foreach (var field in new[] { "FullName", "Email", "Username", "Phone", "Password", "BranchId" })
        {
            errors.TryGetProperty(field, out _).Should().BeTrue($"{field} should be reported");
        }
    }

    [Fact]
    public async Task Duplicate_email_or_username_returns_409()
    {
        await PostAsync(NewUser("dup@example.com", "dupuser", AicosRoles.Customer, null));

        var email = await PostAsync(NewUser("DUP@example.com", "other", AicosRoles.Customer, null));
        email.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await AuthEndpointsTests.ErrorCodeAsync(email)).Should().Be("user.duplicate_email");

        var username = await PostAsync(NewUser("new@example.com", "DupUser", AicosRoles.Customer, null));
        username.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await AuthEndpointsTests.ErrorCodeAsync(username)).Should().Be("user.duplicate_username");
    }

    [Fact]
    public async Task Update_changes_profile_and_role_and_ends_sessions()
    {
        var branch = await CreateBranchAsync();
        var user = await CreateAsync(NewUser("up@example.com", null, AicosRoles.Cashier, branch.Id));
        var login = await LoginAsync("up@example.com");

        var response = await _admin.PutAsJsonAsync($"{Url}/{user.Id}",
            new UpdateUserRequest("Updated Name", "up@example.com", "upuser", "0911111111", AicosRoles.BranchManager, branch.Id), Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = (await response.Content.ReadFromJsonAsync<UserResponse>(Json))!;
        updated.Role.Should().Be(AicosRoles.BranchManager);
        updated.FullName.Should().Be("Updated Name");

        // Role change takes effect on next login: the old refresh token is revoked.
        (await _factory.CreateClient().PostAsJsonAsync("/api/v1/identity/auth/refresh", new RefreshTokenRequest(login.RefreshToken)))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Locked_user_cannot_log_in_until_unlocked()
    {
        var user = await CreateAsync(NewUser("lock@example.com", null, AicosRoles.Customer, null));

        var locked = await _admin.PatchAsJsonAsync($"{Url}/{user.Id}/status", new { status = "Locked" });
        locked.StatusCode.Should().Be(HttpStatusCode.OK);

        var login = await _factory.CreateClient().PostAsJsonAsync("/api/v1/identity/auth/login", new LoginRequest("lock@example.com", Password));
        login.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await AuthEndpointsTests.ErrorCodeAsync(login)).Should().Be("account.locked");

        await _admin.PatchAsJsonAsync($"{Url}/{user.Id}/status", new { status = "Active" });
        (await LoginAsync("lock@example.com")).AccessToken.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Delete_is_soft_hides_the_user_and_keeps_the_email_reserved()
    {
        var user = await CreateAsync(NewUser("del@example.com", null, AicosRoles.Customer, null));

        (await _admin.DeleteAsync($"{Url}/{user.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await _admin.GetAsync($"{Url}/{user.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _factory.CreateClient().PostAsJsonAsync("/api/v1/identity/auth/login", new LoginRequest("del@example.com", Password)))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await PostAsync(NewUser("del@example.com", null, AicosRoles.Customer, null))).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Admin_cannot_lock_delete_or_demote_themselves()
    {
        (await _admin.PatchAsJsonAsync($"{Url}/{_adminId}/status", new { status = "Locked" })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _admin.DeleteAsync($"{Url}/{_adminId}")).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task List_filters_by_role_status_branch_and_search()
    {
        var branch = await CreateBranchAsync();
        await CreateAsync(NewUser("m1@example.com", null, AicosRoles.BranchManager, branch.Id));
        await CreateAsync(NewUser("c1@example.com", null, AicosRoles.Cashier, branch.Id));
        var customer = await CreateAsync(NewUser("k1@example.com", null, AicosRoles.Customer, null));
        await _admin.PatchAsJsonAsync($"{Url}/{customer.Id}/status", new { status = "Locked" });

        (await ListAsync($"?branchId={branch.Id}")).TotalCount.Should().Be(2);
        (await ListAsync("?role=cashier")).Items.Should().ContainSingle().Which.Email.Should().Be("c1@example.com");
        (await ListAsync("?status=Locked")).Items.Should().ContainSingle().Which.Id.Should().Be(customer.Id);
        (await ListAsync("?search=M1@")).Items.Should().ContainSingle();
    }

    [Theory]
    [InlineData(AicosRoles.SuperAdmin)]
    [InlineData(AicosRoles.Barista)]
    [InlineData(AicosRoles.Cashier)]
    [InlineData(AicosRoles.Customer)]
    public async Task Non_management_roles_get_403(string role)
    {
        using var client = _factory.CreateClientAs(role);

        (await client.GetAsync(Url)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PostAsJsonAsync(Url, NewUser("h@example.com", null, AicosRoles.BrandAdmin, null), Json))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ------------------------------------------------------------------ helpers
    private static CreateUserRequest NewUser(string email, string? username, string role, Guid? branchId) =>
        new("Nguyễn Văn A", email, username, "0901234567", Password, role, branchId);

    private Task<HttpResponseMessage> PostAsync(CreateUserRequest request) => _admin.PostAsJsonAsync(Url, request, Json);

    private async Task<UserResponse> CreateAsync(CreateUserRequest request)
    {
        var response = await PostAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<UserResponse>(Json))!;
    }

    private async Task<BranchResponse> CreateBranchAsync()
    {
        var response = await _admin.PostAsJsonAsync("/api/v1/identity/branches", new BranchRequest($"Branch {Guid.NewGuid():N}", null, null, null, null), Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<BranchResponse>(Json))!;
    }

    private async Task<AuthResponse> LoginAsync(string login)
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/api/v1/identity/auth/login", new LoginRequest(login, Password));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>(Json))!;
    }

    private async Task<PagedResponse<UserResponse>> ListAsync(string query) =>
        (await _admin.GetFromJsonAsync<PagedResponse<UserResponse>>(Url + query, Json))!;

    private static async Task<JsonElement> ErrorsAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
}
