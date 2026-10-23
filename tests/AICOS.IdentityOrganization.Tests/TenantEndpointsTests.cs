using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Persistence;
using AICOS.BuildingBlocks.Security;
using AICOS.IdentityOrganization.Api.Domain;
using AICOS.IdentityOrganization.Api.Features.Auth;
using AICOS.IdentityOrganization.Api.Features.Branches;
using AICOS.IdentityOrganization.Api.Features.Tenants;
using AICOS.IdentityOrganization.Api.Features.Users;

namespace AICOS.IdentityOrganization.Tests;

/// <summary>UC-05 tenants, plus the tenant isolation (BR-04) and role scoping (BR-05, BR-06, BR-07) built on them.</summary>
public sealed class TenantEndpointsTests : IDisposable
{
    private const string Tenants = "/api/v1/identity/tenants";
    private const string Users = "/api/v1/identity/users";
    private const string Branches = "/api/v1/identity/branches";
    private const string Auth = "/api/v1/identity/auth";
    private const string Password = "Temp1234";
    private static readonly JsonSerializerOptions Json = AuthEndpointsTests.Json;

    private readonly IdentityApiFactory _factory = new();
    private readonly HttpClient _superAdmin;

    public TenantEndpointsTests() => _superAdmin = _factory.CreateClientAs(AicosRoles.SuperAdmin);

    public void Dispose() => _factory.Dispose();

    // ------------------------------------------------------------------ UC-05
    [Fact]
    public async Task SuperAdmin_creates_a_tenant_with_its_first_brand_admin_who_can_log_in()
    {
        var tenant = await CreateTenantAsync("acme", "admin@acme.com");

        tenant.Status.Should().Be(TenantStatus.Active);
        var login = await LoginAsync("admin@acme.com");
        login.User.Role.Should().Be(AicosRoles.BrandAdmin);
        login.User.TenantId.Should().Be(tenant.Id);
        login.User.MustChangePassword.Should().BeTrue();
    }

    [Fact]
    public async Task Tenant_code_must_be_unique_and_well_formed()
    {
        await CreateTenantAsync("acme", "admin@acme.com");

        (await _superAdmin.PostAsJsonAsync(Tenants, NewTenant("ACME", "other@acme.com"), Json))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _superAdmin.PostAsJsonAsync(Tenants, NewTenant("1 bad code", "x@acme.com"), Json))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _superAdmin.PostAsJsonAsync(Tenants, new CreateTenantRequest("No admin", "noadmin", null), Json))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(AicosRoles.BrandAdmin)]
    [InlineData(AicosRoles.BranchManager)]
    [InlineData(AicosRoles.Customer)]
    public async Task Only_the_super_admin_manages_tenants(string role)
    {
        using var client = _factory.CreateClientAs(role);

        (await client.GetAsync(Tenants)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PostAsJsonAsync(Tenants, NewTenant("hack", "h@h.com"), Json)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Super_admin_lists_gets_and_renames_tenants()
    {
        var tenant = await CreateTenantAsync("acme", "admin@acme.com");

        var list = await _superAdmin.GetFromJsonAsync<PagedResponse<TenantResponse>>($"{Tenants}?search=acme", Json);
        list!.Items.Should().ContainSingle(t => t.Id == tenant.Id);

        var renamed = await _superAdmin.PutAsJsonAsync($"{Tenants}/{tenant.Id}", new UpdateTenantRequest("Acme Coffee"), Json);
        (await renamed.Content.ReadFromJsonAsync<TenantResponse>(Json))!.Name.Should().Be("Acme Coffee");
    }

    // ------------------------------------------------------------------ BR-07
    [Fact]
    public async Task Suspending_a_tenant_blocks_login_and_refresh_for_its_users()
    {
        var tenant = await CreateTenantAsync("acme", "admin@acme.com");
        var session = await LoginAsync("admin@acme.com");

        var suspend = await _superAdmin.PatchAsJsonAsync($"{Tenants}/{tenant.Id}/status", new ChangeTenantStatusRequest(TenantStatus.Suspended), Json);
        suspend.StatusCode.Should().Be(HttpStatusCode.OK);

        await ExpectLoginRejectedAsync("admin@acme.com", "tenant.suspended");
        var refresh = await _factory.CreateClient().PostAsJsonAsync($"{Auth}/refresh", new RefreshTokenRequest(session.RefreshToken), Json);
        refresh.StatusCode.Should().Be(HttpStatusCode.Unauthorized); // sessions were revoked

        await _superAdmin.PatchAsJsonAsync($"{Tenants}/{tenant.Id}/status", new ChangeTenantStatusRequest(TenantStatus.Active), Json);
        (await LoginAsync("admin@acme.com")).User.Email.Should().Be("admin@acme.com");
    }

    // ------------------------------------------------------------------ BR-72
    [Fact]
    public async Task Register_requires_an_existing_active_brand_and_joins_that_brand()
    {
        var tenant = await CreateTenantAsync("acme", "admin@acme.com");
        using var anonymous = _factory.CreateClient();

        (await anonymous.PostAsJsonAsync($"{Auth}/register", new RegisterRequest("nope", "A", "a@x.com", null, Password, Password), Json))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var ok = await anonymous.PostAsJsonAsync($"{Auth}/register", new RegisterRequest("ACME", "A", "a@x.com", null, Password, Password), Json);
        ok.StatusCode.Should().Be(HttpStatusCode.Created);
        (await ok.Content.ReadFromJsonAsync<UserResponse>(Json))!.TenantId.Should().Be(tenant.Id);

        await _superAdmin.PatchAsJsonAsync($"{Tenants}/{tenant.Id}/status", new ChangeTenantStatusRequest(TenantStatus.Suspended), Json);
        (await anonymous.PostAsJsonAsync($"{Auth}/register", new RegisterRequest("acme", "B", "b@x.com", null, Password, Password), Json))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ------------------------------------------------------------------ BR-04 tenant isolation
    [Fact]
    public async Task A_brand_admin_cannot_see_or_touch_another_tenants_branches_and_users()
    {
        var a = await CreateTenantAsync("brand-a", "a@a.com");
        var b = await CreateTenantAsync("brand-b", "b@b.com");
        using var adminA = _factory.CreateClientAs(AicosRoles.BrandAdmin, tenantId: a.Id);
        using var adminB = _factory.CreateClientAs(AicosRoles.BrandAdmin, tenantId: b.Id);

        var branchA = await CreateBranchAsync(adminA, "Chi nhánh A");
        var staffA = await CreateUserAsync(adminA, "cashier@a.com", AicosRoles.Cashier, branchA.Id);

        // B has no branch/users of A, and cannot reach them by id.
        (await adminB.GetFromJsonAsync<PagedResponse<BranchResponse>>(Branches, Json))!.Items.Should().BeEmpty();
        (await adminB.GetAsync($"{Branches}/{branchA.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await adminB.PutAsJsonAsync($"{Branches}/{branchA.Id}", new BranchRequest("Hack", null, null, null, null), Json))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await adminB.GetAsync($"{Users}/{staffA.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await adminB.PatchAsJsonAsync($"{Users}/{staffA.Id}/status", new ChangeUserStatusRequest(UserStatus.Locked), Json))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        // B cannot assign its staff to A's branch.
        var cross = await adminB.PostAsJsonAsync(Users,
            new CreateUserRequest("X", "x@b.com", null, null, Password, AicosRoles.Cashier, branchA.Id), Json);
        cross.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_same_branch_name_can_exist_in_two_tenants()
    {
        var a = await CreateTenantAsync("brand-a", "a@a.com");
        var b = await CreateTenantAsync("brand-b", "b@b.com");
        using var adminA = _factory.CreateClientAs(AicosRoles.BrandAdmin, tenantId: a.Id);
        using var adminB = _factory.CreateClientAs(AicosRoles.BrandAdmin, tenantId: b.Id);

        await CreateBranchAsync(adminA, "Nguyễn Huệ");
        await CreateBranchAsync(adminB, "Nguyễn Huệ");

        (await adminA.PostAsJsonAsync(Branches, new BranchRequest("nguyễn huệ", null, null, null, null), Json))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Active_branches_are_listed_per_brand_code()
    {
        var a = await CreateTenantAsync("brand-a", "a@a.com");
        var b = await CreateTenantAsync("brand-b", "b@b.com");
        using var adminA = _factory.CreateClientAs(AicosRoles.BrandAdmin, tenantId: a.Id);
        using var adminB = _factory.CreateClientAs(AicosRoles.BrandAdmin, tenantId: b.Id);
        await CreateBranchAsync(adminA, "Only A");
        await CreateBranchAsync(adminB, "Only B");
        using var anonymous = _factory.CreateClient();

        var forA = await anonymous.GetFromJsonAsync<List<BranchResponse>>($"{Branches}/active?tenantCode=brand-a", Json);
        forA!.Select(x => x.Name).Should().Equal("Only A");
        (await anonymous.GetAsync($"{Branches}/active")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ------------------------------------------------------------------ BR-06
    [Fact]
    public async Task The_super_admin_cannot_read_brand_business_data()
    {
        (await _superAdmin.GetAsync(Users)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _superAdmin.GetAsync(Branches)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ------------------------------------------------------------------ BR-05 branch manager scope
    [Fact]
    public async Task A_branch_manager_manages_only_baristas_and_cashiers_of_their_own_branch()
    {
        var tenant = await CreateTenantAsync("acme", "admin@acme.com");
        using var admin = _factory.CreateClientAs(AicosRoles.BrandAdmin, tenantId: tenant.Id);
        var own = await CreateBranchAsync(admin, "Own");
        var other = await CreateBranchAsync(admin, "Other");
        var otherStaff = await CreateUserAsync(admin, "other@acme.com", AicosRoles.Cashier, other.Id);
        using var manager = _factory.CreateClientAs(AicosRoles.BranchManager, own.Id, tenantId: tenant.Id);

        // Creates staff of own branch; the branch defaults to their own.
        var created = await manager.PostAsJsonAsync(Users,
            new CreateUserRequest("Bar", "bar@acme.com", null, null, Password, AicosRoles.Barista, null), Json);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        (await created.Content.ReadFromJsonAsync<UserResponse>(Json))!.BranchId.Should().Be(own.Id);

        // Cannot hand out management roles, nor staff another branch.
        (await manager.PostAsJsonAsync(Users, new CreateUserRequest("M", "m@acme.com", null, null, Password, AicosRoles.BranchManager, own.Id), Json))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await manager.PostAsJsonAsync(Users, new CreateUserRequest("C", "c@acme.com", null, null, Password, AicosRoles.Cashier, other.Id), Json))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Sees only own-branch staff; the other branch's cashier is invisible and untouchable.
        var visible = await manager.GetFromJsonAsync<PagedResponse<UserResponse>>(Users, Json);
        visible!.Items.Should().OnlyContain(u => u.BranchId == own.Id).And.NotContain(u => u.Id == otherStaff.Id);
        (await manager.GetAsync($"{Users}/{otherStaff.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await manager.DeleteAsync($"{Users}/{otherStaff.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_access_token_carries_the_tenant_claim()
    {
        var tenant = await CreateTenantAsync("acme", "admin@acme.com");
        var login = await LoginAsync("admin@acme.com");

        var jwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(login.AccessToken);
        jwt.Claims.Single(c => c.Type == AicosClaimTypes.TenantId).Value.Should().Be(tenant.Id.ToString());
    }

    // ------------------------------------------------------------------ helpers
    private static CreateTenantRequest NewTenant(string code, string adminEmail) =>
        new($"Brand {code}", code, new FirstAdminRequest("Brand Owner", adminEmail, "0901234567", Password));

    private async Task<TenantResponse> CreateTenantAsync(string code, string adminEmail)
    {
        var response = await _superAdmin.PostAsJsonAsync(Tenants, NewTenant(code, adminEmail), Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TenantResponse>(Json))!;
    }

    private static async Task<BranchResponse> CreateBranchAsync(HttpClient admin, string name)
    {
        var response = await admin.PostAsJsonAsync(Branches, new BranchRequest(name, null, null, null, null), Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<BranchResponse>(Json))!;
    }

    private static async Task<UserResponse> CreateUserAsync(HttpClient admin, string email, string role, Guid? branchId)
    {
        var response = await admin.PostAsJsonAsync(Users, new CreateUserRequest("Staff", email, null, null, Password, role, branchId), Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<UserResponse>(Json))!;
    }

    private async Task<AuthResponse> LoginAsync(string login)
    {
        var response = await _factory.CreateClient().PostAsJsonAsync($"{Auth}/login", new LoginRequest(login, Password), Json);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>(Json))!;
    }

    private async Task ExpectLoginRejectedAsync(string login, string errorCode)
    {
        var response = await _factory.CreateClient().PostAsJsonAsync($"{Auth}/login", new LoginRequest(login, Password), Json);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString().Should().Be(errorCode);
    }
}
