using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Persistence;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;
using AICOS.IdentityOrganization.Api.Domain;
using AICOS.IdentityOrganization.Api.Features.Audit;
using AICOS.IdentityOrganization.Api.Features.Auth;
using AICOS.IdentityOrganization.Api.Features.Branches;
using AICOS.IdentityOrganization.Api.Features.Tenants;
using AICOS.IdentityOrganization.Api.Features.Users;
using AICOS.IdentityOrganization.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AICOS.IdentityOrganization.Tests;

/// <summary>FR-67 / BR-56 / NFR-12: what is written to the audit history, who can read it, and that it can never be changed.</summary>
public sealed class AuditLogTests : IDisposable
{
    private const string AuditUrl = "/api/v1/identity/audit-logs";
    private const string Users = "/api/v1/identity/users";
    private const string Branches = "/api/v1/identity/branches";
    private const string Auth = "/api/v1/identity/auth";
    private const string Password = "Temp1234";
    private static readonly JsonSerializerOptions Json = AuthEndpointsTests.Json;

    private readonly IdentityApiFactory _factory = new();
    private readonly Guid _adminId = Guid.NewGuid();
    private readonly HttpClient _admin;

    public AuditLogTests() => _admin = _factory.CreateClientAs(AicosRoles.BrandAdmin, userId: _adminId);

    public void Dispose() => _factory.Dispose();

    // ------------------------------------------------------------------ what gets recorded
    [Fact]
    public async Task Creating_an_account_is_recorded_with_actor_and_values_but_no_password()
    {
        var user = await CreateUserAsync("cashier@example.com", AicosRoles.Cashier, (await CreateBranchAsync("B1")).Id);

        var entry = (await ListAsync(_admin, $"?action=user.created&entityId={user.Id}")).Items.Single();

        entry.Scope.Should().Be(AuditScope.Tenant);
        entry.TenantId.Should().Be(IdentityApiFactory.TenantId);
        entry.ActorUserId.Should().Be(_adminId);
        entry.ActorRole.Should().Be(AicosRoles.BrandAdmin);
        entry.EntityType.Should().Be("User");
        entry.Before.Should().BeNull();
        entry.After!.Value.GetProperty("email").GetString().Should().Be("cashier@example.com");
        entry.After.Value.GetProperty("role").GetString().Should().Be(AicosRoles.Cashier);
        entry.After.Value.GetRawText().Should().NotContainEquivalentOf("password").And.NotContain("hash");
    }

    [Fact]
    public async Task Changing_role_status_and_deleting_an_account_each_leave_a_before_after_line()
    {
        var branch = await CreateBranchAsync("B1");
        var user = await CreateUserAsync("staff@example.com", AicosRoles.Cashier, branch.Id);

        await _admin.PutAsJsonAsync($"{Users}/{user.Id}",
            new UpdateUserRequest("Staff New Name", "staff@example.com", null, null, AicosRoles.Barista, branch.Id), Json);
        await _admin.PatchAsJsonAsync($"{Users}/{user.Id}/status", new ChangeUserStatusRequest(UserStatus.Locked), Json);
        await _admin.DeleteAsync($"{Users}/{user.Id}");

        var history = (await ListAsync(_admin, $"?entityType=User&entityId={user.Id}")).Items;
        history.Select(e => e.Action).Should().BeEquivalentTo("user.created", "user.updated", "user.status_changed", "user.deleted");

        var updated = history.Single(e => e.Action == "user.updated");
        updated.Before!.Value.GetProperty("role").GetString().Should().Be(AicosRoles.Cashier);
        updated.After!.Value.GetProperty("role").GetString().Should().Be(AicosRoles.Barista);

        var status = history.Single(e => e.Action == "user.status_changed");
        status.Before!.Value.GetProperty("status").GetString().Should().Be("Active");
        status.After!.Value.GetProperty("status").GetString().Should().Be("Locked");
    }

    [Fact]
    public async Task Branch_changes_are_recorded()
    {
        var branch = await CreateBranchAsync("Quận 1");
        await _admin.PutAsJsonAsync($"{Branches}/{branch.Id}", new BranchRequest("Quận 1 mới", "1 Lê Lợi", null, null, null), Json);
        await _admin.PatchAsJsonAsync($"{Branches}/{branch.Id}/status", new ChangeBranchStatusRequest(BranchStatus.Suspended), Json);

        var history = (await ListAsync(_admin, $"?entityType=Branch&entityId={branch.Id}")).Items;

        history.Select(e => e.Action).Should().BeEquivalentTo("branch.created", "branch.updated", "branch.status_changed");
        history.Single(e => e.Action == "branch.updated").Before!.Value.GetProperty("name").GetString().Should().Be("Quận 1");
        history.Single(e => e.Action == "branch.status_changed").After!.Value.GetProperty("status").GetString().Should().Be("Suspended");
    }

    [Fact]
    public async Task Password_change_and_reset_are_recorded_for_the_account_owner()
    {
        var user = await CreateUserAsync("c@example.com", AicosRoles.Customer, null);
        using var me = await SignedInAsync("c@example.com");
        await me.PostAsJsonAsync($"{Auth}/change-password", new ChangePasswordRequest(Password, "Brand1234", "Brand1234"), Json);

        using var anonymous = _factory.CreateClient();
        await anonymous.PostAsJsonAsync($"{Auth}/forgot-password", new ForgotPasswordRequest("c@example.com"), Json);
        await anonymous.PostAsJsonAsync($"{Auth}/reset-password",
            new ResetPasswordRequest("c@example.com", _factory.Emails.LastCodeFor("c@example.com"), "Reset1234", "Reset1234"), Json);

        var history = (await ListAsync(_admin, $"?entityId={user.Id}&action=user")).Items;

        history.Single(e => e.Action == "user.password_changed").ActorUserId.Should().Be(user.Id);
        history.Single(e => e.Action == "user.password_reset").ActorUserId.Should().BeNull(); // not signed in
        history.Where(e => e.Action.StartsWith("user.password")).Should().OnlyContain(e => e.After == null && e.Before == null); // never the password itself
    }

    [Fact]
    public async Task Automatic_lockout_after_failed_logins_is_recorded_as_a_system_action()
    {
        var user = await CreateUserAsync("c@example.com", AicosRoles.Customer, null);
        using var anonymous = _factory.CreateClient();
        for (var i = 0; i < 5; i++)
        {
            await anonymous.PostAsJsonAsync($"{Auth}/login", new LoginRequest("c@example.com", "Wrong1234"), Json);
        }

        var entry = (await ListAsync(_admin, $"?action=user.locked_out&entityId={user.Id}")).Items.Single();

        entry.ActorUserId.Should().BeNull();
        entry.After!.Value.GetProperty("reason").GetString().Should().Contain("failed login");
    }

    [Fact]
    public async Task A_failed_request_leaves_no_audit_line()
    {
        await _admin.PostAsJsonAsync(Users, new CreateUserRequest("X", "bad", null, null, Password, AicosRoles.Cashier, null), Json); // 400
        await _admin.PostAsJsonAsync(Branches, new BranchRequest("", null, null, null, null), Json);                                    // 400

        (await ListAsync(_admin, "")).TotalCount.Should().Be(0);
    }

    // ------------------------------------------------------------------ who can read it
    [Fact]
    public async Task A_brand_admin_reads_only_their_own_brands_history_and_never_platform_entries()
    {
        using var superAdmin = _factory.CreateClientAs(AicosRoles.SuperAdmin);
        var other = await CreateTenantAsync(superAdmin, "other-brand", "owner@other.com");
        using var otherAdmin = _factory.CreateClientAs(AicosRoles.BrandAdmin, tenantId: other.Id);
        await CreateBranchAsync("Mine");
        await CreateBranchAsync("Theirs", otherAdmin);

        var mine = (await ListAsync(_admin, "")).Items;
        var theirs = (await ListAsync(otherAdmin, "")).Items;

        mine.Should().ContainSingle().Which.After!.Value.GetProperty("name").GetString().Should().Be("Mine");
        theirs.Should().ContainSingle().Which.After!.Value.GetProperty("name").GetString().Should().Be("Theirs");
        mine.Concat(theirs).Should().OnlyContain(e => e.Scope == AuditScope.Tenant); // tenant.created stays platform-only
        (await _admin.GetAsync($"{AuditUrl}/{theirs[0].Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await otherAdmin.GetAsync($"{AuditUrl}/{mine[0].Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_super_admin_reads_platform_history_only_never_business_data()
    {
        using var superAdmin = _factory.CreateClientAs(AicosRoles.SuperAdmin);
        var tenant = await CreateTenantAsync(superAdmin, "acme", "owner@acme.com");
        await superAdmin.PatchAsJsonAsync($"/api/v1/identity/tenants/{tenant.Id}/status", new ChangeTenantStatusRequest(TenantStatus.Suspended), Json);
        await CreateBranchAsync("Business data");

        var entries = (await ListAsync(superAdmin, "")).Items;

        entries.Select(e => e.Action).Should().BeEquivalentTo("tenant.created", "tenant.status_changed");
        entries.Should().OnlyContain(e => e.Scope == AuditScope.Platform);
        entries.Single(e => e.Action == "tenant.status_changed").After!.Value.GetProperty("status").GetString().Should().Be("Suspended");
        (await superAdmin.GetFromJsonAsync<AuditLogResponse>($"{AuditUrl}/{entries[0].Id}", Json))!.Id.Should().Be(entries[0].Id);
    }

    [Theory]
    [InlineData(AicosRoles.BranchManager)]
    [InlineData(AicosRoles.Barista)]
    [InlineData(AicosRoles.Cashier)]
    [InlineData(AicosRoles.Customer)]
    public async Task Other_roles_cannot_read_the_audit_log(string role)
    {
        using var client = _factory.CreateClientAs(role, Guid.NewGuid());

        (await client.GetAsync(AuditUrl)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Anonymous_callers_get_401()
    {
        (await _factory.CreateClient().GetAsync(AuditUrl)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ------------------------------------------------------------------ search and filters
    [Fact]
    public async Task The_history_is_newest_first_and_filterable()
    {
        var branch = await CreateBranchAsync("B1");
        var user = await CreateUserAsync("one@example.com", AicosRoles.Cashier, branch.Id);
        await _admin.PatchAsJsonAsync($"{Users}/{user.Id}/status", new ChangeUserStatusRequest(UserStatus.Locked), Json);

        var all = (await ListAsync(_admin, "")).Items;
        all.Select(e => e.OccurredAtUtc).Should().BeInDescendingOrder();
        all.Should().HaveCount(3);

        (await ListAsync(_admin, "?action=user")).Items.Should().HaveCount(2);                        // prefix matches user.*
        (await ListAsync(_admin, "?action=user.status_changed")).Items.Should().ContainSingle();       // exact
        (await ListAsync(_admin, "?action=use")).Items.Should().BeEmpty();                            // not a dotted prefix
        (await ListAsync(_admin, "?entityType=Branch")).Items.Should().ContainSingle();
        (await ListAsync(_admin, $"?actorUserId={_adminId}")).Items.Should().HaveCount(3);
        (await ListAsync(_admin, $"?actorUserId={Guid.NewGuid()}")).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task The_history_can_be_filtered_by_date_range_and_paged()
    {
        await CreateBranchAsync("B1");
        await CreateBranchAsync("B2");
        await CreateBranchAsync("B3");
        var yesterday = DateTime.UtcNow.AddDays(-1).ToString("o");
        var tomorrow = DateTime.UtcNow.AddDays(1).ToString("o");

        (await ListAsync(_admin, $"?from={Uri.EscapeDataString(yesterday)}&to={Uri.EscapeDataString(tomorrow)}")).TotalCount.Should().Be(3);
        (await ListAsync(_admin, $"?from={Uri.EscapeDataString(tomorrow)}")).TotalCount.Should().Be(0);

        var page = await ListAsync(_admin, "?page=2&pageSize=2");
        page.TotalCount.Should().Be(3);
        page.Items.Should().ContainSingle();

        (await _admin.GetAsync($"{AuditUrl}?from={Uri.EscapeDataString(tomorrow)}&to={Uri.EscapeDataString(yesterday)}"))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ------------------------------------------------------------------ append-only
    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task There_is_no_way_to_write_change_or_delete_entries_through_the_api(string method)
    {
        await CreateBranchAsync("B1");
        var id = (await ListAsync(_admin, "")).Items[0].Id;

        foreach (var url in new[] { AuditUrl, $"{AuditUrl}/{id}" })
        {
            var response = await _admin.SendAsync(new HttpRequestMessage(new HttpMethod(method), url)
            {
                Content = JsonContent.Create(new { action = "forged" }),
            });
            response.StatusCode.Should().BeOneOf(HttpStatusCode.MethodNotAllowed, HttpStatusCode.NotFound);
        }

        (await ListAsync(_admin, "")).TotalCount.Should().Be(1);
    }

    [Fact]
    public async Task The_data_layer_refuses_to_update_or_delete_an_existing_entry()
    {
        await CreateBranchAsync("B1");
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityOrganizationDbContext>();
        var entry = await db.AuditLogs.FirstAsync();

        db.Entry(entry).Property(e => e.Action).CurrentValue = "tampered";
        db.Entry(entry).Property(e => e.Action).IsModified = true;
        await FluentActions.Awaiting(() => db.SaveChangesAsync()).Should().ThrowAsync<InvalidOperationException>().WithMessage("*append-only*");

        db.ChangeTracker.Clear();
        var again = await db.AuditLogs.FirstAsync();
        db.AuditLogs.Remove(again);
        await FluentActions.Awaiting(() => db.SaveChangesAsync()).Should().ThrowAsync<InvalidOperationException>().WithMessage("*append-only*");

        db.ChangeTracker.Clear();
        (await db.AuditLogs.CountAsync()).Should().Be(1);
        (await db.AuditLogs.FirstAsync()).Action.Should().Be("branch.created");
    }

    // ------------------------------------------------------------------ entries sent by other services
    [Fact]
    public async Task An_audit_event_from_another_service_is_stored_once_and_visible_to_that_brand()
    {
        var @event = new AuditRecordedEvent(
            IdentityApiFactory.TenantId, Guid.NewGuid(), "Cashier A", AicosRoles.Cashier, "refund.approved", "Refund", "r-1",
            "{\"status\":\"Pending\"}", "{\"status\":\"Approved\"}");

        await HandleAsync(@event);
        await HandleAsync(@event); // redelivered

        var entry = (await ListAsync(_admin, "?action=refund")).Items.Should().ContainSingle().Subject;
        entry.ActorName.Should().Be("Cashier A");
        entry.EntityId.Should().Be("r-1");
        entry.After!.Value.GetProperty("status").GetString().Should().Be("Approved");
        entry.OccurredAtUtc.Should().BeCloseTo(@event.OccurredOnUtc, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task An_audit_event_without_a_tenant_is_platform_level()
    {
        await HandleAsync(new AuditRecordedEvent(null, null, null, null, "plan.changed", "Plan", "p-1", null, null));

        using var superAdmin = _factory.CreateClientAs(AicosRoles.SuperAdmin);
        (await ListAsync(superAdmin, "?action=plan")).Items.Should().ContainSingle();
        (await ListAsync(_admin, "?action=plan")).Items.Should().BeEmpty();
    }

    // ------------------------------------------------------------------ helpers
    private async Task HandleAsync(AuditRecordedEvent @event)
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IIntegrationEventHandler<AuditRecordedEvent>>().HandleAsync(@event, CancellationToken.None);
    }

    private static async Task<PagedResponse<AuditLogResponse>> ListAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync(AuditUrl + query);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<PagedResponse<AuditLogResponse>>(Json))!;
    }

    private async Task<BranchResponse> CreateBranchAsync(string name, HttpClient? client = null)
    {
        var response = await (client ?? _admin).PostAsJsonAsync(Branches, new BranchRequest(name, null, null, null, null), Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<BranchResponse>(Json))!;
    }

    private async Task<UserResponse> CreateUserAsync(string email, string role, Guid? branchId)
    {
        var response = await _admin.PostAsJsonAsync(Users, new CreateUserRequest("Test User", email, null, null, Password, role, branchId), Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<UserResponse>(Json))!;
    }

    private static async Task<TenantResponse> CreateTenantAsync(HttpClient superAdmin, string code, string adminEmail)
    {
        var response = await superAdmin.PostAsJsonAsync("/api/v1/identity/tenants",
            new CreateTenantRequest($"Brand {code}", code, new FirstAdminRequest("Owner", adminEmail, null, Password)), Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TenantResponse>(Json))!;
    }

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
