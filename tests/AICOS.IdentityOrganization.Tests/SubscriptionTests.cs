using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Persistence;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;
using AICOS.IdentityOrganization.Api.Domain;
using AICOS.IdentityOrganization.Api.Features.Audit;
using AICOS.IdentityOrganization.Api.Features.Branches;
using AICOS.IdentityOrganization.Api.Features.Platform;
using AICOS.IdentityOrganization.Api.Features.Subscriptions;
using AICOS.IdentityOrganization.Api.Features.Tenants;
using AICOS.IdentityOrganization.Api.Features.Users;
using Microsoft.Extensions.DependencyInjection;

namespace AICOS.IdentityOrganization.Tests;

/// <summary>UC-06 plans, UC-07 tenant subscriptions, FR-77 limits, UC-10 revenue and FR-05 renewal reminders.</summary>
public sealed class SubscriptionTests : IDisposable
{
    private const string Plans = "/api/v1/identity/subscription-plans";
    private const string Tenants = "/api/v1/identity/tenants";
    private const string Users = "/api/v1/identity/users";
    private const string Branches = "/api/v1/identity/branches";
    private const string Password = "Temp1234";
    private static readonly JsonSerializerOptions Json = AuthEndpointsTests.Json;

    private readonly IdentityApiFactory _factory = new();
    private readonly HttpClient _superAdmin;

    public SubscriptionTests() => _superAdmin = _factory.CreateClientAs(AicosRoles.SuperAdmin);

    public void Dispose() => _factory.Dispose();

    // ------------------------------------------------------------------ plans (UC-06)
    [Fact]
    public async Task The_landing_page_lists_only_active_plans_without_logging_in_and_without_internal_fields()
    {
        var basic = await CreatePlanAsync("Basic", fee: 500_000);
        var pro = await CreatePlanAsync("Pro", fee: 900_000);
        var retired = await CreatePlanAsync("Old", fee: 100_000);
        (await _superAdmin.PatchAsJsonAsync($"{Plans}/{retired.Id}/status", new PlanStatusRequest(false), Json)).EnsureSuccessStatusCode();
        var anonymous = _factory.CreateClient();

        var response = await anonymous.GetAsync("/api/v1/identity/public/subscription-plans");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var plans = await response.Content.ReadFromJsonAsync<JsonElement>();
        plans.EnumerateArray().Select(p => p.GetProperty("name").GetString()).Should().Equal("Basic", "Pro");
        plans[0].GetProperty("fee").GetDecimal().Should().Be(500_000);
        plans[0].TryGetProperty("subscribers", out _).Should().BeFalse();
        plans[0].TryGetProperty("isActive", out _).Should().BeFalse();

        // The admin list stays closed to anonymous callers.
        (await anonymous.GetAsync(Plans)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Super_admin_creates_lists_updates_and_deactivates_a_plan()
    {
        var plan = await CreatePlanAsync("Basic", fee: 500_000, maxBranches: 1, maxStaff: 3);

        plan.IsActive.Should().BeTrue();
        plan.Tier.Should().Be("Basic");
        plan.BillingPeriod.Should().Be(BillingPeriod.Monthly);
        plan.MaxBranches.Should().Be(1);
        plan.Subscribers.Should().Be(0);

        var updated = await _superAdmin.PutAsJsonAsync($"{Plans}/{plan.Id}",
            new PlanRequest("Basic+", "Basic", 600_000, BillingPeriod.Quarterly, null, 5, "More seats"), Json);
        var body = (await updated.Content.ReadFromJsonAsync<PlanResponse>(Json))!;
        body.Name.Should().Be("Basic+");
        body.Fee.Should().Be(600_000);
        body.BillingPeriod.Should().Be(BillingPeriod.Quarterly);
        body.MaxBranches.Should().BeNull(); // unlimited

        var off = await _superAdmin.PatchAsJsonAsync($"{Plans}/{plan.Id}/status", new PlanStatusRequest(false), Json);
        (await off.Content.ReadFromJsonAsync<PlanResponse>(Json))!.IsActive.Should().BeFalse();

        var active = await _superAdmin.GetFromJsonAsync<PagedResponse<PlanResponse>>($"{Plans}?isActive=true", Json);
        active!.Items.Should().BeEmpty();
        (await _superAdmin.GetFromJsonAsync<PagedResponse<PlanResponse>>($"{Plans}?search=basic", Json))!.Items.Should().ContainSingle();
    }

    [Fact]
    public async Task Plan_fields_are_validated()
    {
        var response = await _superAdmin.PostAsJsonAsync(Plans, new PlanRequest("", "", -1, null, 0, 0, new string('d', 501)), Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        foreach (var field in new[] { "Name", "Tier", "Fee", "BillingPeriod", "MaxBranches", "MaxStaffAccounts", "Description" })
        {
            errors.TryGetProperty(field, out _).Should().BeTrue($"{field} should be reported");
        }
    }

    [Fact]
    public async Task Plan_names_are_unique_ignoring_case()
    {
        await CreatePlanAsync("Basic");

        (await _superAdmin.PostAsJsonAsync(Plans, NewPlan("BASIC"), Json)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Theory]
    [InlineData(AicosRoles.BrandAdmin)]
    [InlineData(AicosRoles.BranchManager)]
    [InlineData(AicosRoles.Cashier)]
    [InlineData(AicosRoles.Customer)]
    public async Task Only_the_super_admin_manages_plans_and_subscriptions(string role)
    {
        using var client = _factory.CreateClientAs(role, Guid.NewGuid());

        (await client.GetAsync(Plans)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PostAsJsonAsync(Plans, NewPlan("Hack"), Json)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync("/api/v1/identity/subscriptions")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync("/api/v1/identity/platform/overview")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PostAsJsonAsync($"{Tenants}/{Guid.NewGuid()}/subscription", new AssignPlanRequest(Guid.NewGuid(), null), Json))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ------------------------------------------------------------------ assign / change plan (UC-07)
    [Fact]
    public async Task A_plan_is_assigned_with_a_renewal_date_one_period_ahead_and_starts_unpaid()
    {
        var tenant = await CreateTenantAsync("acme");
        var plan = await CreatePlanAsync("Basic", fee: 500_000);

        var response = await AssignAsync(tenant.Id, plan.Id);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var sub = (await response.Content.ReadFromJsonAsync<SubscriptionResponse>(Json))!;

        sub.PlanName.Should().Be("Basic");
        sub.Fee.Should().Be(500_000);
        sub.PaymentStatus.Should().Be(SubscriptionPaymentStatus.Unpaid);
        sub.Outstanding.Should().Be(500_000);
        sub.PeriodEndUtc.Should().BeCloseTo(sub.PeriodStartUtc.AddMonths(1), TimeSpan.FromSeconds(1));
        sub.Health.Should().Be(SubscriptionHealth.Current);
        sub.DaysUntilRenewal.Should().BeInRange(28, 31);
        sub.StaffAccounts.Should().Be(1); // the first Brand Admin
        (await _superAdmin.GetFromJsonAsync<PlanResponse>($"{Plans}/{plan.Id}", Json))!.Subscribers.Should().Be(1);
    }

    [Fact]
    public async Task Yearly_and_quarterly_plans_set_the_matching_renewal_date_and_a_free_plan_starts_paid()
    {
        var tenant = await CreateTenantAsync("acme");
        var yearly = await CreatePlanAsync("Yearly", fee: 6_000_000, period: BillingPeriod.Yearly);
        var free = await CreatePlanAsync("Trial", fee: 0, period: BillingPeriod.Quarterly);

        var sub = await AssignOkAsync(tenant.Id, yearly.Id);
        sub.PeriodEndUtc.Should().BeCloseTo(sub.PeriodStartUtc.AddYears(1), TimeSpan.FromSeconds(1));

        var trial = await AssignOkAsync(tenant.Id, free.Id);
        trial.PeriodEndUtc.Should().BeCloseTo(trial.PeriodStartUtc.AddMonths(3), TimeSpan.FromSeconds(1));
        trial.PaymentStatus.Should().Be(SubscriptionPaymentStatus.Paid);
    }

    [Fact]
    public async Task Changing_plan_replaces_the_current_subscription_and_keeps_the_old_one_as_history()
    {
        var tenant = await CreateTenantAsync("acme");
        var basic = await CreatePlanAsync("Basic");
        var premium = await CreatePlanAsync("Premium", fee: 2_000_000);
        await AssignOkAsync(tenant.Id, basic.Id);

        var current = await AssignOkAsync(tenant.Id, premium.Id);

        current.PlanName.Should().Be("Premium");
        (await _superAdmin.GetFromJsonAsync<SubscriptionResponse>($"{Tenants}/{tenant.Id}/subscription", Json))!.PlanName.Should().Be("Premium");
        (await _superAdmin.GetFromJsonAsync<PlanResponse>($"{Plans}/{basic.Id}", Json))!.Subscribers.Should().Be(0);
        var list = await _superAdmin.GetFromJsonAsync<PagedResponse<SubscriptionSummaryResponse>>("/api/v1/identity/subscriptions", Json);
        list!.Items.Should().ContainSingle().Which.PlanName.Should().Be("Premium"); // one current subscription per tenant
    }

    [Fact]
    public async Task Assigning_is_refused_for_the_same_plan_an_inactive_plan_an_unknown_plan_or_tenant()
    {
        var tenant = await CreateTenantAsync("acme");
        var basic = await CreatePlanAsync("Basic");
        var retired = await CreatePlanAsync("Retired");
        await _superAdmin.PatchAsJsonAsync($"{Plans}/{retired.Id}/status", new PlanStatusRequest(false), Json);
        await AssignOkAsync(tenant.Id, basic.Id);

        (await AssignAsync(tenant.Id, basic.Id)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await AssignAsync(tenant.Id, retired.Id)).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await AssignAsync(tenant.Id, Guid.NewGuid())).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _superAdmin.PostAsJsonAsync($"{Tenants}/{tenant.Id}/subscription", new AssignPlanRequest(null, null), Json))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await AssignAsync(Guid.NewGuid(), basic.Id)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_tenant_without_a_subscription_has_none_to_show()
    {
        var tenant = await CreateTenantAsync("acme");

        (await _superAdmin.GetAsync($"{Tenants}/{tenant.Id}/subscription")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _superAdmin.PostAsJsonAsync($"{Tenants}/{tenant.Id}/subscription/payments", NewPayment(1000), Json)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _superAdmin.PostAsync($"{Tenants}/{tenant.Id}/subscription/renew", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_tenant_can_start_on_a_plan_when_it_is_created_and_a_bad_plan_creates_nothing()
    {
        var plan = await CreatePlanAsync("Basic");

        var ok = await _superAdmin.PostAsJsonAsync(Tenants,
            new CreateTenantRequest("Acme", "acme", new FirstAdminRequest("Owner", "owner@acme.com", null, Password), plan.Id), Json);
        ok.StatusCode.Should().Be(HttpStatusCode.Created);
        var tenant = (await ok.Content.ReadFromJsonAsync<TenantResponse>(Json))!;
        (await _superAdmin.GetFromJsonAsync<SubscriptionResponse>($"{Tenants}/{tenant.Id}/subscription", Json))!.PlanName.Should().Be("Basic");

        var bad = await _superAdmin.PostAsJsonAsync(Tenants,
            new CreateTenantRequest("Other", "other", new FirstAdminRequest("Owner", "owner@other.com", null, Password), Guid.NewGuid()), Json);
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _superAdmin.GetFromJsonAsync<PagedResponse<TenantResponse>>($"{Tenants}?search=other", Json))!.Items.Should().BeEmpty();
    }

    // ------------------------------------------------------------------ payments and renewal (UC-07, BR-09, BR-10)
    [Fact]
    public async Task Payments_add_up_until_the_period_is_paid_and_overpaying_is_refused()
    {
        var tenant = await CreateTenantAsync("acme");
        await AssignOkAsync(tenant.Id, (await CreatePlanAsync("Basic", fee: 500_000)).Id);

        var first = await _superAdmin.PostAsJsonAsync($"{Tenants}/{tenant.Id}/subscription/payments", NewPayment(200_000), Json);
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        var partial = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("subscription");
        partial.GetProperty("paymentStatus").GetString().Should().Be("Unpaid");
        partial.GetProperty("outstanding").GetDecimal().Should().Be(300_000);

        (await _superAdmin.PostAsJsonAsync($"{Tenants}/{tenant.Id}/subscription/payments", NewPayment(300_001), Json))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity); // more than is outstanding

        var last = await _superAdmin.PostAsJsonAsync($"{Tenants}/{tenant.Id}/subscription/payments", NewPayment(300_000), Json);
        (await last.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("subscription").GetProperty("paymentStatus").GetString().Should().Be("Paid");

        (await _superAdmin.PostAsJsonAsync($"{Tenants}/{tenant.Id}/subscription/payments", NewPayment(1), Json))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity); // nothing left to pay

        var history = await _superAdmin.GetFromJsonAsync<PagedResponse<SubscriptionPaymentResponse>>($"{Tenants}/{tenant.Id}/subscription/payments", Json);
        history!.Items.Select(p => p.Amount).Should().BeEquivalentTo([200_000m, 300_000m]);
    }

    [Fact]
    public async Task Payment_input_is_validated()
    {
        var tenant = await CreateTenantAsync("acme");
        await AssignOkAsync(tenant.Id, (await CreatePlanAsync("Basic")).Id);
        var url = $"{Tenants}/{tenant.Id}/subscription/payments";

        (await _superAdmin.PostAsJsonAsync(url, new RecordPaymentRequest(0, "BankTransfer", null, null), Json)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _superAdmin.PostAsJsonAsync(url, new RecordPaymentRequest(-5, "BankTransfer", null, null), Json)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _superAdmin.PostAsJsonAsync(url, new RecordPaymentRequest(1000, "", null, null), Json)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _superAdmin.PostAsJsonAsync(url, new RecordPaymentRequest(1000, "BankTransfer", new string('r', 101), null), Json)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _superAdmin.PostAsJsonAsync(url, new RecordPaymentRequest(1000, "BankTransfer", null, DateTime.UtcNow.AddDays(10)), Json)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Renewing_needs_the_current_period_paid_then_starts_the_next_period_unpaid()
    {
        var tenant = await CreateTenantAsync("acme");
        var start = await AssignOkAsync(tenant.Id, (await CreatePlanAsync("Basic", fee: 500_000)).Id);

        (await _superAdmin.PostAsync($"{Tenants}/{tenant.Id}/subscription/renew", null)).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        await _superAdmin.PostAsJsonAsync($"{Tenants}/{tenant.Id}/subscription/payments", NewPayment(500_000), Json);
        var renewed = await (await _superAdmin.PostAsync($"{Tenants}/{tenant.Id}/subscription/renew", null)).Content.ReadFromJsonAsync<SubscriptionResponse>(Json);

        renewed!.PeriodStartUtc.Should().BeCloseTo(start.PeriodEndUtc, TimeSpan.FromSeconds(1)); // continues from the old renewal date
        renewed.PeriodEndUtc.Should().BeCloseTo(start.PeriodEndUtc.AddMonths(1), TimeSpan.FromSeconds(1));
        renewed.PaymentStatus.Should().Be(SubscriptionPaymentStatus.Unpaid);
        renewed.PaidAmount.Should().Be(0);
    }

    [Fact]
    public async Task A_late_renewal_starts_today_so_no_free_back_dated_period_is_created()
    {
        var tenant = await CreateTenantAsync("acme");
        await AssignOkAsync(tenant.Id, (await CreatePlanAsync("Basic", fee: 500_000)).Id);
        await _superAdmin.PostAsJsonAsync($"{Tenants}/{tenant.Id}/subscription/payments", NewPayment(500_000), Json);
        _factory.Clock.Advance(TimeSpan.FromDays(90)); // three months late

        var renewed = (await (await _superAdmin.PostAsync($"{Tenants}/{tenant.Id}/subscription/renew", null)).Content.ReadFromJsonAsync<SubscriptionResponse>(Json))!;

        renewed.PeriodStartUtc.Should().BeCloseTo(DateTime.UtcNow.AddDays(90), TimeSpan.FromMinutes(1));
        renewed.Health.Should().Be(SubscriptionHealth.Current);
    }

    [Fact]
    public async Task The_renewal_date_moves_the_subscription_through_due_soon_grace_period_and_overdue()
    {
        var tenant = await CreateTenantAsync("acme");
        var sub = await AssignOkAsync(tenant.Id, (await CreatePlanAsync("Basic")).Id);
        var untilEnd = sub.PeriodEndUtc - DateTime.UtcNow;

        (await CurrentAsync(tenant.Id)).Health.Should().Be(SubscriptionHealth.Current);

        _factory.Clock.Advance(untilEnd - TimeSpan.FromDays(3));
        (await CurrentAsync(tenant.Id)).Health.Should().Be(SubscriptionHealth.DueSoon);

        _factory.Clock.Advance(TimeSpan.FromDays(4)); // 1 day past the renewal date
        var grace = await CurrentAsync(tenant.Id);
        grace.Health.Should().Be(SubscriptionHealth.GracePeriod);
        grace.DaysUntilRenewal.Should().BeLessThanOrEqualTo(0);

        _factory.Clock.Advance(TimeSpan.FromDays(7)); // grace period (7 days) is over
        (await CurrentAsync(tenant.Id)).Health.Should().Be(SubscriptionHealth.Overdue);
    }

    [Fact]
    public async Task The_subscription_list_filters_by_health_payment_status_and_name()
    {
        var soon = await CreateTenantAsync("soon-brand");
        var later = await CreateTenantAsync("later-brand");
        var plan = await CreatePlanAsync("Basic", fee: 500_000);
        var soonSub = await AssignOkAsync(soon.Id, plan.Id);
        await AssignOkAsync(later.Id, plan.Id, start: DateTime.UtcNow.AddDays(20));
        await _superAdmin.PostAsJsonAsync($"{Tenants}/{later.Id}/subscription/payments", NewPayment(500_000), Json);
        _factory.Clock.Advance(soonSub.PeriodEndUtc - DateTime.UtcNow - TimeSpan.FromDays(2));

        var dueSoon = await ListSubscriptionsAsync("?health=DueSoon");
        dueSoon.Items.Should().ContainSingle().Which.TenantCode.Should().Be("soon-brand");
        (await ListSubscriptionsAsync("?health=Current")).Items.Should().ContainSingle().Which.TenantCode.Should().Be("later-brand");
        (await ListSubscriptionsAsync("?health=Overdue")).Items.Should().BeEmpty();
        (await ListSubscriptionsAsync("?paymentStatus=Paid")).Items.Should().ContainSingle().Which.TenantCode.Should().Be("later-brand");
        (await ListSubscriptionsAsync("?search=SOON")).Items.Should().ContainSingle();
        (await ListSubscriptionsAsync("")).Items.Select(i => i.TenantCode).Should().Equal("soon-brand", "later-brand"); // nearest renewal first
    }

    // ------------------------------------------------------------------ plan limits (FR-77, BR-11)
    [Fact]
    public async Task The_branch_limit_counts_only_active_branches_and_applies_to_reactivation()
    {
        var tenant = await CreateTenantAsync("acme");
        await AssignOkAsync(tenant.Id, (await CreatePlanAsync("Basic", maxBranches: 1)).Id);
        using var admin = _factory.CreateClientAs(AicosRoles.BrandAdmin, tenantId: tenant.Id);

        var first = await CreateBranchAsync(admin, "First");
        var blocked = await admin.PostAsJsonAsync(Branches, new BranchRequest("Second", null, null, null, null), Json);
        blocked.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await blocked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString().Should().Be("subscription.limit_reached");

        await admin.PatchAsJsonAsync($"{Branches}/{first.Id}/status", new ChangeBranchStatusRequest(BranchStatus.Suspended), Json); // frees the slot
        var second = await CreateBranchAsync(admin, "Second");
        (await admin.PatchAsJsonAsync($"{Branches}/{first.Id}/status", new ChangeBranchStatusRequest(BranchStatus.Active), Json))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity); // bringing the first one back would exceed the plan
        (await admin.PatchAsJsonAsync($"{Branches}/{second.Id}/status", new ChangeBranchStatusRequest(BranchStatus.Active), Json))
            .StatusCode.Should().Be(HttpStatusCode.OK); // already active: not a new one
    }

    [Fact]
    public async Task The_staff_limit_counts_staff_roles_only_and_a_deleted_account_frees_its_seat()
    {
        var tenant = await CreateTenantAsync("acme");
        await AssignOkAsync(tenant.Id, (await CreatePlanAsync("Basic", maxStaff: 3)).Id); // the first Brand Admin is seat 1
        using var admin = _factory.CreateClientAs(AicosRoles.BrandAdmin, tenantId: tenant.Id);
        var branch = await CreateBranchAsync(admin, "HQ");

        await CreateUserAsync(admin, "a@acme.com", AicosRoles.Cashier, branch.Id);
        var seatThree = await CreateUserAsync(admin, "b@acme.com", AicosRoles.Barista, branch.Id);

        (await admin.PostAsJsonAsync(Users, new CreateUserRequest("C", "c@acme.com", null, null, Password, AicosRoles.Cashier, branch.Id), Json))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        // Customers are not staff: unlimited.
        (await admin.PostAsJsonAsync(Users, new CreateUserRequest("K", "k@acme.com", null, null, Password, AicosRoles.Customer, null), Json))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        // ...and cannot be promoted into a seat that does not exist either.
        var customer = await (await admin.GetAsync($"{Users}?role=Customer")).Content.ReadFromJsonAsync<PagedResponse<UserResponse>>(Json);
        (await admin.PutAsJsonAsync($"{Users}/{customer!.Items[0].Id}",
            new UpdateUserRequest("K", "k@acme.com", null, null, AicosRoles.Cashier, branch.Id), Json)).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        await admin.DeleteAsync($"{Users}/{seatThree.Id}");
        (await admin.PostAsJsonAsync(Users, new CreateUserRequest("C", "c@acme.com", null, null, Password, AicosRoles.Cashier, branch.Id), Json))
            .StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task A_branch_manager_adding_staff_is_held_to_the_same_limit()
    {
        var tenant = await CreateTenantAsync("acme");
        await AssignOkAsync(tenant.Id, (await CreatePlanAsync("Basic", maxStaff: 1)).Id);
        using var admin = _factory.CreateClientAs(AicosRoles.BrandAdmin, tenantId: tenant.Id);
        var branch = await CreateBranchAsync(admin, "HQ");
        using var manager = _factory.CreateClientAs(AicosRoles.BranchManager, branch.Id, tenantId: tenant.Id);

        (await manager.PostAsJsonAsync(Users, new CreateUserRequest("B", "b@acme.com", null, null, Password, AicosRoles.Barista, null), Json))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Moving_to_a_plan_the_tenant_already_exceeds_is_refused_and_raising_the_plan_limit_takes_effect()
    {
        var tenant = await CreateTenantAsync("acme");
        var premium = await CreatePlanAsync("Premium", fee: 2_000_000);
        var basic = await CreatePlanAsync("Basic", maxBranches: 1);
        await AssignOkAsync(tenant.Id, premium.Id);
        using var admin = _factory.CreateClientAs(AicosRoles.BrandAdmin, tenantId: tenant.Id);
        await CreateBranchAsync(admin, "One");
        await CreateBranchAsync(admin, "Two");

        var refused = await AssignAsync(tenant.Id, basic.Id);
        refused.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString().Should().Be("subscription.limits_exceeded");
        (await CurrentAsync(tenant.Id)).PlanName.Should().Be("Premium"); // unchanged

        await _superAdmin.PutAsJsonAsync($"{Plans}/{basic.Id}", new PlanRequest("Basic", "Basic", 500_000, BillingPeriod.Monthly, 2, null, null), Json);
        (await AssignAsync(tenant.Id, basic.Id)).StatusCode.Should().Be(HttpStatusCode.Created);
        (await admin.PostAsJsonAsync(Branches, new BranchRequest("Three", null, null, null, null), Json)).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Tenants_without_a_plan_are_not_limited()
    {
        var tenant = await CreateTenantAsync("acme");
        using var admin = _factory.CreateClientAs(AicosRoles.BrandAdmin, tenantId: tenant.Id);

        for (var i = 0; i < 4; i++)
        {
            await CreateBranchAsync(admin, $"Branch {i}");
        }
    }

    [Fact]
    public async Task Changing_a_plans_fee_does_not_rewrite_what_an_existing_tenant_agreed_to()
    {
        var tenant = await CreateTenantAsync("acme");
        var plan = await CreatePlanAsync("Basic", fee: 500_000);
        await AssignOkAsync(tenant.Id, plan.Id);

        await _superAdmin.PutAsJsonAsync($"{Plans}/{plan.Id}", new PlanRequest("Basic", "Basic", 900_000, BillingPeriod.Monthly, null, null, null), Json);

        (await CurrentAsync(tenant.Id)).Fee.Should().Be(500_000);
    }

    // ------------------------------------------------------------------ overview and revenue (UC-09, UC-10, BR-09)
    [Fact]
    public async Task The_overview_summarises_tenants_subscriptions_and_saas_revenue()
    {
        var a = await CreateTenantAsync("brand-a");
        var b = await CreateTenantAsync("brand-b");
        var c = await CreateTenantAsync("brand-c"); // never gets a plan
        await _superAdmin.PatchAsJsonAsync($"{Tenants}/{c.Id}/status", new ChangeTenantStatusRequest(TenantStatus.Suspended), Json);
        var basic = await CreatePlanAsync("Basic", fee: 600_000);
        var premium = await CreatePlanAsync("Premium", fee: 3_000_000, period: BillingPeriod.Quarterly);
        await AssignOkAsync(a.Id, basic.Id);
        await AssignOkAsync(b.Id, premium.Id);
        await _superAdmin.PostAsJsonAsync($"{Tenants}/{a.Id}/subscription/payments", NewPayment(600_000), Json);
        await _superAdmin.PostAsJsonAsync($"{Tenants}/{b.Id}/subscription/payments", NewPayment(1_000_000), Json);

        var overview = (await _superAdmin.GetFromJsonAsync<PlatformOverviewResponse>("/api/v1/identity/platform/overview", Json))!;

        overview.Tenants.Should().BeEquivalentTo(new TenantCounts(4, 3, 1, 2)); // + the seeded test brand; two have no plan
        overview.Subscriptions.Current.Should().Be(2);
        overview.Subscriptions.UnpaidCurrentPeriods.Should().Be(1);
        overview.Subscriptions.OutstandingAmount.Should().Be(2_000_000);
        overview.Revenue.ThisMonth.Should().Be(1_600_000);
        overview.Revenue.AllTime.Should().Be(1_600_000);
        overview.Revenue.Last12Months.Should().HaveCount(12);
        overview.Revenue.Last12Months[^1].Amount.Should().Be(1_600_000);
        overview.Revenue.Last12Months.Take(11).Should().OnlyContain(m => m.Amount == 0);
        overview.Plans.Should().Contain(p => p.PlanName == "Basic" && p.MonthlyRecurringRevenue == 600_000)
            .And.Contain(p => p.PlanName == "Premium" && p.MonthlyRecurringRevenue == 1_000_000); // quarterly fee / 3
    }

    [Fact]
    public async Task The_revenue_report_groups_by_month_and_plan_and_rejects_bad_ranges()
    {
        var tenant = await CreateTenantAsync("acme");
        await AssignOkAsync(tenant.Id, (await CreatePlanAsync("Basic", fee: 500_000)).Id);
        await _superAdmin.PostAsJsonAsync($"{Tenants}/{tenant.Id}/subscription/payments",
            new RecordPaymentRequest(200_000, "BankTransfer", "REF-1", DateTime.UtcNow.AddMonths(-2)), Json);
        await _superAdmin.PostAsJsonAsync($"{Tenants}/{tenant.Id}/subscription/payments", NewPayment(300_000), Json);

        var report = (await _superAdmin.GetFromJsonAsync<RevenueReportResponse>("/api/v1/identity/platform/revenue", Json))!;

        report.Total.Should().Be(500_000);
        report.ByMonth.Should().HaveCount(12);
        report.ByMonth.Count(m => m.Amount > 0).Should().Be(2);
        report.ByPlan.Should().ContainSingle().Which.Should().BeEquivalentTo(new RevenueByPlan("Basic", 500_000, 2));

        var from = Uri.EscapeDataString(DateTime.UtcNow.AddDays(1).ToString("o"));
        var to = Uri.EscapeDataString(DateTime.UtcNow.AddDays(-1).ToString("o"));
        (await _superAdmin.GetAsync($"/api/v1/identity/platform/revenue?from={from}&to={to}")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var wide = Uri.EscapeDataString(DateTime.UtcNow.AddYears(-6).ToString("o"));
        (await _superAdmin.GetAsync($"/api/v1/identity/platform/revenue?from={wide}")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ------------------------------------------------------------------ audit (FR-67)
    [Fact]
    public async Task Plan_and_subscription_changes_are_recorded_in_the_platform_audit_history()
    {
        var tenant = await CreateTenantAsync("acme");
        var plan = await CreatePlanAsync("Basic", fee: 500_000);
        await _superAdmin.PatchAsJsonAsync($"{Plans}/{plan.Id}/status", new PlanStatusRequest(false), Json);
        await _superAdmin.PatchAsJsonAsync($"{Plans}/{plan.Id}/status", new PlanStatusRequest(true), Json);
        await AssignOkAsync(tenant.Id, plan.Id);
        await _superAdmin.PostAsJsonAsync($"{Tenants}/{tenant.Id}/subscription/payments", NewPayment(500_000), Json);
        await _superAdmin.PostAsync($"{Tenants}/{tenant.Id}/subscription/renew", null);

        var history = (await _superAdmin.GetFromJsonAsync<PagedResponse<AuditLogResponse>>("/api/v1/identity/audit-logs?pageSize=50", Json))!.Items;

        history.Select(h => h.Action).Should().Contain(
            ["plan.created", "plan.status_changed", "subscription.assigned", "subscription.payment_recorded", "subscription.renewed"]);
        history.Should().OnlyContain(h => h.Scope == AuditScope.Platform);
        history.Single(h => h.Action == "subscription.renewed").Before!.Value.GetProperty("paymentStatus").GetString().Should().Be("Paid");
        history.Single(h => h.Action == "subscription.renewed").After!.Value.GetProperty("paymentStatus").GetString().Should().Be("Unpaid");
    }

    // ------------------------------------------------------------------ renewal reminders (FR-05, BR-10)
    [Fact]
    public async Task A_renewal_reminder_is_published_once_per_period_when_the_date_is_close()
    {
        var tenant = await CreateTenantAsync("acme");
        var sub = await AssignOkAsync(tenant.Id, (await CreatePlanAsync("Basic", fee: 500_000)).Id);

        (await RunReminderAsync()).Should().Be(new ReminderRunResult(0, 0)); // renewal date still far away

        _factory.Clock.Advance(sub.PeriodEndUtc - DateTime.UtcNow - TimeSpan.FromDays(3));
        (await RunReminderAsync()).Should().Be(new ReminderRunResult(1, 0));
        (await RunReminderAsync()).Should().Be(new ReminderRunResult(0, 0)); // not repeated

        var notice = _factory.Events.Published.OfType<SubscriptionRenewalDueEvent>().Single();
        notice.TenantId.Should().Be(tenant.Id);
        notice.PlanName.Should().Be("Basic");
        notice.DaysLeft.Should().Be(3);

        // Renewing opens a new period, so its own reminder is due again later.
        await _superAdmin.PostAsJsonAsync($"{Tenants}/{tenant.Id}/subscription/payments", NewPayment(500_000), Json);
        var renewed = (await (await _superAdmin.PostAsync($"{Tenants}/{tenant.Id}/subscription/renew", null)).Content.ReadFromJsonAsync<SubscriptionResponse>(Json))!;
        (await RunReminderAsync()).Should().Be(new ReminderRunResult(0, 0));
        _factory.Clock.Advance(renewed.PeriodEndUtc - _factory.Clock.GetUtcNow().UtcDateTime - TimeSpan.FromDays(2)); // relative to the moved clock
        (await RunReminderAsync()).Should().Be(new ReminderRunResult(1, 0));
    }

    [Fact]
    public async Task An_overdue_notice_is_published_once_after_the_renewal_date_passes()
    {
        var tenant = await CreateTenantAsync("acme");
        var sub = await AssignOkAsync(tenant.Id, (await CreatePlanAsync("Basic", fee: 500_000)).Id);
        _factory.Clock.Advance(sub.PeriodEndUtc - DateTime.UtcNow + TimeSpan.FromDays(2)); // two days past the renewal date

        (await RunReminderAsync()).Should().Be(new ReminderRunResult(0, 1));
        _factory.Clock.Advance(TimeSpan.FromDays(10)); // now past the grace period as well
        (await RunReminderAsync()).Should().Be(new ReminderRunResult(0, 0)); // already told for this period

        var notice = _factory.Events.Published.OfType<SubscriptionOverdueEvent>().Single();
        notice.TenantId.Should().Be(tenant.Id);
        notice.DaysOverdue.Should().Be(2);
        notice.GracePeriodOver.Should().BeFalse(); // still inside the grace period when it was sent
    }

    [Fact]
    public async Task Suspended_tenants_and_tenants_without_a_plan_get_no_reminders()
    {
        var suspended = await CreateTenantAsync("gone");
        await CreateTenantAsync("planless");
        var sub = await AssignOkAsync(suspended.Id, (await CreatePlanAsync("Basic")).Id);
        await _superAdmin.PatchAsJsonAsync($"{Tenants}/{suspended.Id}/status", new ChangeTenantStatusRequest(TenantStatus.Suspended), Json);
        _factory.Clock.Advance(sub.PeriodEndUtc - DateTime.UtcNow + TimeSpan.FromDays(2));

        (await RunReminderAsync()).Should().Be(new ReminderRunResult(0, 0));
        _factory.Events.Published.OfType<SubscriptionOverdueEvent>().Should().BeEmpty();
    }

    // ------------------------------------------------------------------ helpers
    private static PlanRequest NewPlan(string name, decimal fee = 500_000, BillingPeriod period = BillingPeriod.Monthly, int? maxBranches = null, int? maxStaff = null) =>
        new(name, name, fee, period, maxBranches, maxStaff, null);

    private static RecordPaymentRequest NewPayment(decimal amount) => new(amount, "BankTransfer", "REF", null);

    private async Task<PlanResponse> CreatePlanAsync(
        string name, decimal fee = 500_000, BillingPeriod period = BillingPeriod.Monthly, int? maxBranches = null, int? maxStaff = null)
    {
        var response = await _superAdmin.PostAsJsonAsync(Plans, NewPlan(name, fee, period, maxBranches, maxStaff), Json);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<PlanResponse>(Json))!;
    }

    private async Task<TenantResponse> CreateTenantAsync(string code)
    {
        var response = await _superAdmin.PostAsJsonAsync(Tenants,
            new CreateTenantRequest($"Brand {code}", code, new FirstAdminRequest("Owner", $"owner@{code}.com", null, Password)), Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TenantResponse>(Json))!;
    }

    private Task<HttpResponseMessage> AssignAsync(Guid tenantId, Guid planId, DateTime? start = null) =>
        _superAdmin.PostAsJsonAsync($"{Tenants}/{tenantId}/subscription", new AssignPlanRequest(planId, start), Json);

    private async Task<SubscriptionResponse> AssignOkAsync(Guid tenantId, Guid planId, DateTime? start = null)
    {
        var response = await AssignAsync(tenantId, planId, start);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<SubscriptionResponse>(Json))!;
    }

    private async Task<SubscriptionResponse> CurrentAsync(Guid tenantId) =>
        (await _superAdmin.GetFromJsonAsync<SubscriptionResponse>($"{Tenants}/{tenantId}/subscription", Json))!;

    private async Task<PagedResponse<SubscriptionSummaryResponse>> ListSubscriptionsAsync(string query) =>
        (await _superAdmin.GetFromJsonAsync<PagedResponse<SubscriptionSummaryResponse>>($"/api/v1/identity/subscriptions{query}", Json))!;

    private async Task<ReminderRunResult> RunReminderAsync()
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SubscriptionReminderJob>().RunAsync(CancellationToken.None);
    }

    private static async Task<BranchResponse> CreateBranchAsync(HttpClient admin, string name)
    {
        var response = await admin.PostAsJsonAsync(Branches, new BranchRequest(name, null, null, null, null), Json);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<BranchResponse>(Json))!;
    }

    private static async Task<UserResponse> CreateUserAsync(HttpClient admin, string email, string role, Guid? branchId)
    {
        var response = await admin.PostAsJsonAsync(Users, new CreateUserRequest("Staff", email, null, null, Password, role, branchId), Json);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<UserResponse>(Json))!;
    }
}
