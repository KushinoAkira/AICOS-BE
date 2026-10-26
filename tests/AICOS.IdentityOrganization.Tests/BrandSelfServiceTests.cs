using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Persistence;
using AICOS.BuildingBlocks.Security;
using AICOS.IdentityOrganization.Api.Domain;
using AICOS.IdentityOrganization.Api.Features.Auth;
using AICOS.IdentityOrganization.Api.Features.Branches;
using AICOS.IdentityOrganization.Api.Features.Subscriptions;
using AICOS.IdentityOrganization.Api.Features.Tenants;
using AICOS.IdentityOrganization.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AICOS.IdentityOrganization.Tests;

/// <summary>Self-service brands (register, Pending to Approved or Rejected), the public page of a brand, the profile and the contact form. All of it lives in the database.</summary>
public sealed class BrandSelfServiceTests : IDisposable
{
    private const string Base = "/api/v1/identity";
    private const string Password = "Brand1234";
    private static readonly JsonSerializerOptions Json = AuthEndpointsTests.Json;

    private readonly IdentityApiFactory _factory = new();
    private readonly HttpClient _anonymous;
    private readonly HttpClient _superAdmin;

    public BrandSelfServiceTests()
    {
        _anonymous = _factory.CreateClient();
        _superAdmin = _factory.CreateClientAs(AicosRoles.SuperAdmin);
    }

    public void Dispose() => _factory.Dispose();

    private static RegisterBrandRequest Registration(string code = "cafe-sai-gon", string email = "owner@cafesaigon.vn", string password = Password, string? confirm = null) =>
        new("Cafe Sài Gòn", code, "Nguyễn Văn Chủ", email, "0901234567", password, confirm ?? password, "Quán cà phê rang xay");

    private Task<HttpResponseMessage> RegisterAsync(RegisterBrandRequest? request = null) =>
        _anonymous.PostAsJsonAsync($"{Base}/auth/register-brand", request ?? Registration(), Json);

    private Task<HttpResponseMessage> LoginAsync(string email, string password = Password) =>
        _anonymous.PostAsJsonAsync($"{Base}/auth/login", new LoginRequest(email, password), Json);

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString();

    private async Task<TenantResponse> RegisterOkAsync(string code = "cafe-sai-gon", string email = "owner@cafesaigon.vn")
    {
        var response = await RegisterAsync(Registration(code, email));
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var tenants = await _superAdmin.GetFromJsonAsync<PagedResponse<TenantResponse>>($"{Base}/tenants?search={code}", Json);
        return tenants!.Items.Single();
    }

    private async Task<TenantStatus> StatusInDatabaseAsync(string code)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IdentityOrganizationDbContext>().Tenants.Where(t => t.Code == code).Select(t => t.Status).SingleAsync();
    }

    // ------------------------------------------------------------------ registration
    [Fact]
    public async Task An_owner_registers_a_brand_that_waits_for_approval_and_cannot_sign_in_yet()
    {
        var response = await RegisterAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        (body.GetProperty("status").GetString(), body.GetProperty("code").GetString()).Should().Be(("Pending", "cafe-sai-gon"));
        body.TryGetProperty("accessToken", out _).Should().BeFalse();                                   // no session before approval
        (await StatusInDatabaseAsync("cafe-sai-gon")).Should().Be(TenantStatus.Pending);

        var login = await LoginAsync("owner@cafesaigon.vn");
        login.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(login)).Should().Be("tenant.pending_approval");
        (await _anonymous.GetAsync($"{Base}/public/brands/cafe-sai-gon")).StatusCode.Should().Be(HttpStatusCode.NotFound);   // a pending brand has no public page
        _factory.Emails.Sent.Should().ContainSingle().Which.To.Should().Be("owner@cafesaigon.vn");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityOrganizationDbContext>();
        var owner = await db.Users.SingleAsync(u => u.Email == "owner@cafesaigon.vn");
        (owner.Role, owner.TenantId).Should().Be((AicosRoles.BrandAdmin, (await db.Tenants.SingleAsync(t => t.Code == "cafe-sai-gon")).Id));
        owner.PasswordHash.Should().NotContain(Password);                                              // only the hash is stored
    }

    [Fact]
    public async Task A_registration_is_checked()
    {
        (await RegisterAsync(Registration(code: "x"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);                                          // code too short
        (await RegisterAsync(Registration(code: "1abc"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);                                      // must start with a letter
        (await RegisterAsync(Registration(email: "not-an-email"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await RegisterAsync(Registration(password: "short"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await RegisterAsync(Registration(confirm: "Different1234"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await RegisterAsync(new RegisterBrandRequest(null, null, null, null, null, null, null, null))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await RegisterAsync(Registration() with { Description = new string('x', 501) })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var scope = _factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<IdentityOrganizationDbContext>().Tenants.CountAsync()).Should().Be(1);   // only the seeded test brand
    }

    [Fact]
    public async Task A_brand_code_or_an_owner_email_that_is_taken_is_refused()
    {
        await RegisterOkAsync();

        var sameCode = await RegisterAsync(Registration(email: "other@example.com"));
        var sameEmail = await RegisterAsync(Registration(code: "another-brand"));
        var seededCode = await RegisterAsync(Registration(code: IdentityApiFactory.TenantCode, email: "third@example.com"));

        sameCode.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorCodeAsync(sameCode)).Should().Be("tenant.duplicate_code");
        sameEmail.StatusCode.Should().Be(HttpStatusCode.Conflict);
        seededCode.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var scope = _factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<IdentityOrganizationDbContext>().Tenants.AnyAsync(t => t.Code == "another-brand")).Should().BeFalse();   // nothing half-created
    }

    // ------------------------------------------------------------------ the decision of the super admin
    [Fact]
    public async Task The_super_admin_sees_the_pending_registrations_and_approves_one()
    {
        var tenant = await RegisterOkAsync();
        var pending = await _superAdmin.GetFromJsonAsync<PagedResponse<TenantResponse>>($"{Base}/tenants?status=Pending", Json);
        pending!.Items.Select(t => t.Code).Should().Equal("cafe-sai-gon");

        var approved = await _superAdmin.PostAsJsonAsync($"{Base}/tenants/{tenant.Id}/approve", new { });

        approved.StatusCode.Should().Be(HttpStatusCode.OK);
        (await approved.Content.ReadFromJsonAsync<TenantResponse>(Json))!.Status.Should().Be(TenantStatus.Active);
        (await LoginAsync("owner@cafesaigon.vn")).StatusCode.Should().Be(HttpStatusCode.OK);              // now the owner can sign in
        (await _anonymous.GetAsync($"{Base}/public/brands/cafe-sai-gon")).StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Emails.Sent.Last().Subject.Should().Contain("đã được duyệt");

        var audit = await _superAdmin.GetFromJsonAsync<JsonElement>($"{Base}/audit-logs?pageSize=50");
        audit.GetProperty("items").EnumerateArray().Select(a => a.GetProperty("action").GetString()).Should().Contain(["tenant.registered", "tenant.approved"]);
        (await _superAdmin.PostAsJsonAsync($"{Base}/tenants/{tenant.Id}/approve", new { })).StatusCode.Should().Be(HttpStatusCode.Conflict);   // decided once
    }

    [Fact]
    public async Task A_rejected_registration_needs_a_reason_and_stays_closed()
    {
        var tenant = await RegisterOkAsync();

        (await _superAdmin.PostAsJsonAsync($"{Base}/tenants/{tenant.Id}/reject", new { })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var rejected = await _superAdmin.PostAsJsonAsync($"{Base}/tenants/{tenant.Id}/reject", new { reason = "Thông tin không hợp lệ" });

        rejected.StatusCode.Should().Be(HttpStatusCode.OK);
        (await StatusInDatabaseAsync("cafe-sai-gon")).Should().Be(TenantStatus.Rejected);
        var login = await LoginAsync("owner@cafesaigon.vn");
        (login.StatusCode, await ErrorCodeAsync(login)).Should().Be((HttpStatusCode.Forbidden, "tenant.rejected"));
        (await _anonymous.GetAsync($"{Base}/public/brands/cafe-sai-gon")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        _factory.Emails.Sent.Last().Body.Should().Contain("Thông tin không hợp lệ");
        (await _superAdmin.PostAsJsonAsync($"{Base}/tenants/{tenant.Id}/approve", new { })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorCodeAsync(await _superAdmin.PostAsJsonAsync($"{Base}/tenants/{tenant.Id}/reject", new { reason = "again" }))).Should().Be("tenant.not_pending");
    }

    [Fact]
    public async Task A_registration_cannot_be_activated_by_the_plain_status_change_and_only_the_super_admin_decides()
    {
        var tenant = await RegisterOkAsync();

        var patch = await _superAdmin.PatchAsJsonAsync($"{Base}/tenants/{tenant.Id}/status", new ChangeTenantStatusRequest(TenantStatus.Active), Json);
        patch.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorCodeAsync(patch)).Should().Be("tenant.use_review");
        (await _superAdmin.PatchAsJsonAsync($"{Base}/tenants/{IdentityApiFactory.TenantId}/status", new ChangeTenantStatusRequest(TenantStatus.Pending), Json)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        foreach (var role in new[] { AicosRoles.BrandAdmin, AicosRoles.BranchManager, AicosRoles.Customer })
        {
            using var other = _factory.CreateClientAs(role);
            (await other.PostAsJsonAsync($"{Base}/tenants/{tenant.Id}/approve", new { })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await other.PostAsJsonAsync($"{Base}/tenants/{tenant.Id}/reject", new { reason = "x" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        (await _anonymous.PostAsJsonAsync($"{Base}/tenants/{tenant.Id}/approve", new { })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await StatusInDatabaseAsync("cafe-sai-gon")).Should().Be(TenantStatus.Pending);
    }

    // ------------------------------------------------------------------ the profile and the public page
    private async Task<HttpClient> ApprovedBrandAdminAsync()
    {
        var tenant = await RegisterOkAsync();
        (await _superAdmin.PostAsJsonAsync($"{Base}/tenants/{tenant.Id}/approve", new { })).EnsureSuccessStatusCode();
        return _factory.CreateClientAs(AicosRoles.BrandAdmin, tenantId: tenant.Id);
    }

    [Fact]
    public async Task The_brand_admin_edits_the_profile_and_the_public_page_shows_it_with_the_active_branches()
    {
        var admin = await ApprovedBrandAdminAsync();
        var tenantId = (await admin.GetFromJsonAsync<JsonElement>($"{Base}/brand/profile")).GetProperty("id").GetGuid();
        var open = await (await admin.PostAsJsonAsync($"{Base}/branches", new BranchRequest("Quận 1", "1 Lê Lợi", "0901234567", new TimeOnly(7, 0), new TimeOnly(22, 0)), Json)).Content.ReadFromJsonAsync<BranchResponse>(Json);
        var closed = await (await admin.PostAsJsonAsync($"{Base}/branches", new BranchRequest("Quận 3", "3 Nguyễn Du", null, null, null), Json)).Content.ReadFromJsonAsync<BranchResponse>(Json);
        (await admin.PatchAsJsonAsync($"{Base}/branches/{closed!.Id}/status", new ChangeBranchStatusRequest(BranchStatus.Suspended), Json)).EnsureSuccessStatusCode();

        var saved = await admin.PutAsJsonAsync($"{Base}/brand/profile", new BrandProfileRequest("  Cà phê rang xay  ", "https://cdn.example.com/logo.png", "#6f4e37", "#FFF8E7"), Json);

        saved.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await _anonymous.GetFromJsonAsync<JsonElement>($"{Base}/public/brands/CAFE-SAI-GON");       // the code is not case sensitive
        page.GetProperty("name").GetString().Should().Be("Cafe Sài Gòn");
        page.GetProperty("description").GetString().Should().Be("Cà phê rang xay");
        page.GetProperty("logoUrl").GetString().Should().Be("https://cdn.example.com/logo.png");
        (page.GetProperty("primaryColor").GetString(), page.GetProperty("secondaryColor").GetString()).Should().Be(("#6F4E37", "#FFF8E7"));
        page.GetProperty("branches").EnumerateArray().Select(b => b.GetProperty("name").GetString()).Should().Equal("Quận 1");
        page.GetProperty("branches")[0].GetProperty("id").GetGuid().Should().Be(open!.Id);
        page.ToString().Should().NotContain(tenantId.ToString());                                            // nothing internal leaks on the public page
        page.TryGetProperty("status", out _).Should().BeFalse();
    }

    [Fact]
    public async Task The_profile_is_checked_and_only_the_own_brand_admin_edits_it()
    {
        var admin = await ApprovedBrandAdminAsync();

        foreach (var bad in new[]
                 {
                     new BrandProfileRequest(null, "javascript:alert(1)", null, null),
                     new BrandProfileRequest(null, "data:image/png;base64,AAAA", null, null),
                     new BrandProfileRequest(null, "ftp://example.com/logo.png", null, null),
                     new BrandProfileRequest(null, "https://user:pass@example.com/logo.png", null, null),
                     new BrandProfileRequest(null, "https://example.com/" + new string('a', 500), null, null),
                     new BrandProfileRequest(null, null, "red", null),
                     new BrandProfileRequest(null, null, null, "#12345"),
                     new BrandProfileRequest(new string('x', 501), null, null, null),
                 })
        {
            (await admin.PutAsJsonAsync($"{Base}/brand/profile", bad, Json)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        foreach (var role in new[] { AicosRoles.BranchManager, AicosRoles.Cashier, AicosRoles.Customer, AicosRoles.SuperAdmin })
        {
            using var other = _factory.CreateClientAs(role);
            (await other.PutAsJsonAsync($"{Base}/brand/profile", new BrandProfileRequest("x", null, null, null), Json)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        (await _anonymous.GetAsync($"{Base}/brand/profile")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        // Clearing is allowed: empty values remove the logo and colors.
        (await admin.PutAsJsonAsync($"{Base}/brand/profile", new BrandProfileRequest(null, "", null, ""), Json)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _anonymous.GetFromJsonAsync<JsonElement>($"{Base}/public/brands/cafe-sai-gon")).GetProperty("logoUrl").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_brand_admin_changes_only_their_own_brand_and_a_suspended_brand_disappears_from_the_public()
    {
        var admin = await ApprovedBrandAdminAsync();
        await admin.PutAsJsonAsync($"{Base}/brand/profile", new BrandProfileRequest("Mine", null, null, null), Json);

        var seededProfile = await _factory.CreateClientAs(AicosRoles.BrandAdmin).GetFromJsonAsync<JsonElement>($"{Base}/brand/profile");   // the seeded brand
        seededProfile.GetProperty("code").GetString().Should().Be(IdentityApiFactory.TenantCode);
        seededProfile.GetProperty("description").ValueKind.Should().Be(JsonValueKind.Null);

        var id = (await admin.GetFromJsonAsync<JsonElement>($"{Base}/brand/profile")).GetProperty("id").GetGuid();
        (await _superAdmin.PatchAsJsonAsync($"{Base}/tenants/{id}/status", new ChangeTenantStatusRequest(TenantStatus.Suspended), Json)).EnsureSuccessStatusCode();
        (await _anonymous.GetAsync($"{Base}/public/brands/cafe-sai-gon")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _anonymous.GetAsync($"{Base}/public/brands/does-not-exist")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ------------------------------------------------------------------ contact form
    [Fact]
    public async Task The_contact_form_is_stored_in_the_database_and_the_super_admin_reads_and_closes_messages()
    {
        var sent = await _anonymous.PostAsJsonAsync($"{Base}/public/contact",
            new ContactRequest(" Trần Thị Khách ", "khach@example.com", "0912345678", "Hỏi giá", "Cho mình xin báo giá gói Pro."), Json);

        sent.StatusCode.Should().Be(HttpStatusCode.Accepted);
        using (var scope = _factory.Services.CreateScope())
        {
            var saved = await scope.ServiceProvider.GetRequiredService<IdentityOrganizationDbContext>().ContactMessages.SingleAsync();
            (saved.FullName, saved.Status, saved.Subject).Should().Be(("Trần Thị Khách", ContactMessageStatus.New, "Hỏi giá"));
        }

        var list = await _superAdmin.GetFromJsonAsync<JsonElement>($"{Base}/contact-messages?status=New");
        list.GetProperty("totalCount").GetInt32().Should().Be(1);
        var id = list.GetProperty("items")[0].GetProperty("id").GetGuid();
        var handled = await (await _superAdmin.PostAsync($"{Base}/contact-messages/{id}/handled", null)).Content.ReadFromJsonAsync<JsonElement>();
        handled.GetProperty("status").GetString().Should().Be("Handled");
        (await _superAdmin.GetFromJsonAsync<JsonElement>($"{Base}/contact-messages?status=New")).GetProperty("totalCount").GetInt32().Should().Be(0);
        (await _superAdmin.GetFromJsonAsync<JsonElement>($"{Base}/contact-messages?status=Handled")).GetProperty("totalCount").GetInt32().Should().Be(1);
        (await _superAdmin.PostAsync($"{Base}/contact-messages/{Guid.NewGuid()}/handled", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_contact_form_is_checked_ignores_bots_and_is_read_only_by_the_super_admin()
    {
        var invalid = new[]
        {
            new ContactRequest(null, "a@example.com", null, null, "hi"),
            new ContactRequest("A", "not-an-email", null, null, "hi"),
            new ContactRequest("A", "a@example.com", "abc", null, "hi"),
            new ContactRequest("A", "a@example.com", null, null, ""),
            new ContactRequest("A", "a@example.com", null, null, new string('x', 2001)),
            new ContactRequest("A", "a@example.com", null, new string('x', 151), "hi"),
        };
        foreach (var request in invalid)
        {
            (await _anonymous.PostAsJsonAsync($"{Base}/public/contact", request, Json)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        var bot = await _anonymous.PostAsJsonAsync($"{Base}/public/contact", new ContactRequest("Bot", "bot@example.com", null, null, "buy now", Website: "http://spam.example"), Json);
        bot.StatusCode.Should().Be(HttpStatusCode.Accepted);                                              // the bot is told it worked
        using (var scope = _factory.Services.CreateScope())
        {
            (await scope.ServiceProvider.GetRequiredService<IdentityOrganizationDbContext>().ContactMessages.CountAsync()).Should().Be(0);
        }

        foreach (var role in new[] { AicosRoles.BrandAdmin, AicosRoles.BranchManager, AicosRoles.Customer })
        {
            using var other = _factory.CreateClientAs(role);
            (await other.GetAsync($"{Base}/contact-messages")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        (await _anonymous.GetAsync($"{Base}/contact-messages")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ------------------------------------------------------------------ the price list on the landing page
    [Fact]
    public async Task The_price_list_at_the_admin_path_is_open_to_everyone_but_shows_only_what_is_on_sale()
    {
        var basic = await (await _superAdmin.PostAsJsonAsync($"{Base}/subscription-plans", new PlanRequest("Basic", "Basic", 500_000, BillingPeriod.Monthly, 1, 3, null), Json)).Content.ReadFromJsonAsync<PlanResponse>(Json);
        var retired = await (await _superAdmin.PostAsJsonAsync($"{Base}/subscription-plans", new PlanRequest("Old", "Basic", 100_000, BillingPeriod.Monthly, 1, 3, null), Json)).Content.ReadFromJsonAsync<PlanResponse>(Json);
        (await _superAdmin.PatchAsJsonAsync($"{Base}/subscription-plans/{retired!.Id}/status", new PlanStatusRequest(false), Json)).EnsureSuccessStatusCode();
        await _superAdmin.PostAsJsonAsync($"{Base}/tenants/{IdentityApiFactory.TenantId}/subscription", new { planId = basic!.Id }, Json);

        var visitor = await _anonymous.GetAsync($"{Base}/subscription-plans");
        var brand = await _factory.CreateClientAs(AicosRoles.BrandAdmin).GetFromJsonAsync<JsonElement>($"{Base}/subscription-plans?isActive=false");
        var platform = await _superAdmin.GetFromJsonAsync<JsonElement>($"{Base}/subscription-plans");

        visitor.StatusCode.Should().Be(HttpStatusCode.OK);
        var shown = (await visitor.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray().ToList();
        shown.Select(p => p.GetProperty("name").GetString()).Should().Equal("Basic");
        shown.Single().GetProperty("subscribers").GetInt32().Should().Be(0);                              // the count of customers is not public
        brand.GetProperty("items").EnumerateArray().Select(p => p.GetProperty("name").GetString()).Should().Equal("Basic");   // asking for the retired ones changes nothing
        platform.GetProperty("items").EnumerateArray().Select(p => p.GetProperty("name").GetString()).Should().BeEquivalentTo("Basic", "Old");
        platform.GetProperty("items").EnumerateArray().Single(p => p.GetProperty("name").GetString() == "Basic").GetProperty("subscribers").GetInt32().Should().Be(1);

        (await _anonymous.PostAsJsonAsync($"{Base}/subscription-plans", new PlanRequest("Hack", "Basic", 1, BillingPeriod.Monthly, 1, 1, null), Json)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _anonymous.GetAsync($"{Base}/subscription-plans/{basic.Id}")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
