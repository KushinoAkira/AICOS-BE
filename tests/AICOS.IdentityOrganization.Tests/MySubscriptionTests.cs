using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Persistence;
using AICOS.BuildingBlocks.Security;
using AICOS.IdentityOrganization.Api.Domain;
using AICOS.IdentityOrganization.Api.Features.Subscriptions;
using AICOS.IdentityOrganization.Api.Features.Tenants;

namespace AICOS.IdentityOrganization.Tests;

/// <summary>SCRUM-132 / UC-22: the Brand Admin sees the subscription of their own brand and renews it by simulated payment.</summary>
public sealed class MySubscriptionTests : IDisposable
{
    private const string Me = "/api/v1/identity/subscription";
    private static readonly JsonSerializerOptions Json = AuthEndpointsTests.Json;

    private readonly IdentityApiFactory _factory = new();
    private readonly HttpClient _superAdmin;

    public MySubscriptionTests() => _superAdmin = _factory.CreateClientAs(AicosRoles.SuperAdmin);

    public void Dispose() => _factory.Dispose();

    private async Task<(TenantResponse Tenant, HttpClient Admin)> BrandOnPlanAsync(string code, decimal fee = 500_000, BillingPeriod period = BillingPeriod.Monthly)
    {
        var plan = await _superAdmin.PostAsJsonAsync("/api/v1/identity/subscription-plans", new PlanRequest($"Plan {code}", "Basic", fee, period, 3, 10, null), Json);
        plan.EnsureSuccessStatusCode();
        var planId = (await plan.Content.ReadFromJsonAsync<PlanResponse>(Json))!.Id;
        var tenantResponse = await _superAdmin.PostAsJsonAsync("/api/v1/identity/tenants",
            new CreateTenantRequest($"Brand {code}", code, new FirstAdminRequest("Owner", $"owner@{code}.com", null, "Temp1234"), planId), Json);
        tenantResponse.EnsureSuccessStatusCode();
        var tenant = (await tenantResponse.Content.ReadFromJsonAsync<TenantResponse>(Json))!;
        return (tenant, _factory.CreateClientAs(AicosRoles.BrandAdmin, tenantId: tenant.Id));
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task The_brand_admin_sees_plan_fee_period_payment_status_renewal_date_and_usage_of_their_brand()
    {
        var (tenant, admin) = await BrandOnPlanAsync("acme");

        var mine = await BodyAsync(await admin.GetAsync(Me));

        var sub = mine.GetProperty("subscription");
        sub.GetProperty("tenantId").GetGuid().Should().Be(tenant.Id);
        sub.GetProperty("planName").GetString().Should().Be("Plan acme");
        (sub.GetProperty("fee").GetDecimal(), sub.GetProperty("billingPeriod").GetString(), sub.GetProperty("paymentStatus").GetString()).Should().Be((500_000m, "Monthly", "Unpaid"));
        (sub.GetProperty("outstanding").GetDecimal(), sub.GetProperty("health").GetString()).Should().Be((500_000m, "Current"));
        (sub.GetProperty("maxBranches").GetInt32(), sub.GetProperty("maxStaffAccounts").GetInt32()).Should().Be((3, 10));
        sub.GetProperty("staffAccounts").GetInt32().Should().Be(1);                                      // the owner
        sub.GetProperty("periodEndUtc").GetDateTime().Should().BeAfter(sub.GetProperty("periodStartUtc").GetDateTime());
        (mine.GetProperty("canRenew").GetBoolean(), mine.GetProperty("amountToPay").GetDecimal()).Should().Be((true, 500_000m));    // an unpaid period can be paid at once
    }

    [Fact]
    public async Task A_simulated_renewal_of_an_unpaid_period_pays_what_is_outstanding_and_is_kept_in_the_payment_history()
    {
        var (_, admin) = await BrandOnPlanAsync("acme");

        var result = await BodyAsync(await admin.PostAsJsonAsync($"{Me}/renew", new { }));

        result.GetProperty("action").GetString().Should().Be("PaidOutstanding");
        result.GetProperty("amountPaid").GetDecimal().Should().Be(500_000);
        result.GetProperty("payment").GetProperty("method").GetString().Should().Be("Simulated");
        var sub = result.GetProperty("subscription").GetProperty("subscription");
        (sub.GetProperty("paymentStatus").GetString(), sub.GetProperty("outstanding").GetDecimal()).Should().Be(("Paid", 0m));
        result.GetProperty("subscription").GetProperty("canRenew").GetBoolean().Should().BeFalse();                // paid and far from the end
        result.GetProperty("subscription").GetProperty("renewableFromUtc").ValueKind.Should().Be(JsonValueKind.String);

        var history = await admin.GetFromJsonAsync<PagedResponse<SubscriptionPaymentResponse>>($"{Me}/payments", Json);
        history!.TotalCount.Should().Be(1);
        history.Items[0].Method.Should().Be("Simulated");
    }

    [Fact]
    public async Task A_paid_period_cannot_be_renewed_before_the_renewal_window_opens()
    {
        var (_, admin) = await BrandOnPlanAsync("acme");
        (await admin.PostAsJsonAsync($"{Me}/renew", new { })).EnsureSuccessStatusCode();

        var early = await admin.PostAsJsonAsync($"{Me}/renew", new { });

        early.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await BodyAsync(early)).GetProperty("errorCode").GetString().Should().Be("subscription.renewal_not_open");
        (await admin.GetFromJsonAsync<PagedResponse<SubscriptionPaymentResponse>>($"{Me}/payments", Json))!.TotalCount.Should().Be(1);   // nothing was charged
    }

    [Fact]
    public async Task Inside_the_renewal_window_the_next_period_is_started_and_paid()
    {
        var (_, admin) = await BrandOnPlanAsync("acme");
        var first = await BodyAsync(await admin.PostAsJsonAsync($"{Me}/renew", new { }));
        var end = first.GetProperty("subscription").GetProperty("subscription").GetProperty("periodEndUtc").GetDateTime();
        _factory.Clock.Advance(end - DateTime.UtcNow - TimeSpan.FromDays(3));                                     // three days before the end: inside the 7-day notice

        var mine = await BodyAsync(await admin.GetAsync(Me));
        (mine.GetProperty("canRenew").GetBoolean(), mine.GetProperty("amountToPay").GetDecimal()).Should().Be((true, 500_000m));
        mine.GetProperty("subscription").GetProperty("health").GetString().Should().Be("DueSoon");

        var renewed = await BodyAsync(await admin.PostAsJsonAsync($"{Me}/renew", new { }));

        renewed.GetProperty("action").GetString().Should().Be("Renewed");
        var sub = renewed.GetProperty("subscription").GetProperty("subscription");
        sub.GetProperty("periodEndUtc").GetDateTime().Should().BeAfter(end);                                       // the next period
        sub.GetProperty("paymentStatus").GetString().Should().Be("Paid");
        sub.GetProperty("health").GetString().Should().Be("Current");
        (await admin.GetFromJsonAsync<PagedResponse<SubscriptionPaymentResponse>>($"{Me}/payments", Json))!.TotalCount.Should().Be(2);
    }

    [Fact]
    public async Task A_brand_never_sees_or_pays_the_subscription_of_another_brand()
    {
        var (a, adminA) = await BrandOnPlanAsync("alpha", fee: 100_000);
        var (b, adminB) = await BrandOnPlanAsync("beta", fee: 900_000);

        (await BodyAsync(await adminA.GetAsync(Me))).GetProperty("subscription").GetProperty("tenantId").GetGuid().Should().Be(a.Id);
        (await adminB.PostAsJsonAsync($"{Me}/renew", new { })).EnsureSuccessStatusCode();

        (await BodyAsync(await adminA.GetAsync(Me))).GetProperty("subscription").GetProperty("paymentStatus").GetString().Should().Be("Unpaid");
        (await adminA.GetFromJsonAsync<PagedResponse<SubscriptionPaymentResponse>>($"{Me}/payments", Json))!.TotalCount.Should().Be(0);
        (await _superAdmin.GetFromJsonAsync<PagedResponse<SubscriptionPaymentResponse>>($"/api/v1/identity/tenants/{b.Id}/subscription/payments", Json))!.TotalCount.Should().Be(1);
    }

    [Fact]
    public async Task Only_the_brand_admin_uses_these_endpoints_and_a_brand_without_a_plan_gets_404()
    {
        var (tenant, _) = await BrandOnPlanAsync("acme");
        foreach (var role in new[] { AicosRoles.BranchManager, AicosRoles.Cashier, AicosRoles.Barista, AicosRoles.Customer })
        {
            using var other = _factory.CreateClientAs(role, Guid.NewGuid(), tenantId: tenant.Id);
            (await other.GetAsync(Me)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await other.PostAsJsonAsync($"{Me}/renew", new { })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        (await _superAdmin.GetAsync(Me)).StatusCode.Should().Be(HttpStatusCode.Forbidden);                        // the platform owner uses /tenants/{id}/subscription
        (await _factory.CreateClient().GetAsync(Me)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var noPlan = _factory.CreateClientAs(AicosRoles.BrandAdmin, tenantId: Guid.NewGuid());
        (await noPlan.GetAsync(Me)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await noPlan.PostAsJsonAsync($"{Me}/renew", new { })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_free_plan_has_nothing_to_pay()
    {
        var (_, admin) = await BrandOnPlanAsync("free", fee: 0);
        var mine = await BodyAsync(await admin.GetAsync(Me));
        (mine.GetProperty("canRenew").GetBoolean(), mine.GetProperty("amountToPay").GetDecimal()).Should().Be((false, 0m));

        (await admin.PostAsJsonAsync($"{Me}/renew", new { })).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Simulated_payments_are_in_the_audit_history_of_the_brand()
    {
        var (_, admin) = await BrandOnPlanAsync("acme");
        (await admin.PostAsJsonAsync($"{Me}/renew", new { })).EnsureSuccessStatusCode();

        var audit = await admin.GetStringAsync("/api/v1/identity/audit-logs?pageSize=50");

        audit.Should().Contain("subscription.paid_simulated");
    }
}
