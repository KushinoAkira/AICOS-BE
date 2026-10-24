using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;
using AICOS.ReportingAnalytics.Api;
using AICOS.ReportingAnalytics.Api.Persistence;

namespace AICOS.Services.IntegrationTests;

/// <summary>SCRUM-50: chain and branch reports built from order and payment events.</summary>
public sealed class ReportTests : IDisposable
{
    private const string Reports = "/api/v1/reporting";
    private const string Day = "from=2026-11-02&to=2026-11-02";

    private readonly ServiceFactory<ReportingAnalyticsService, ReportingAnalyticsDbContext> _f = new();
    private readonly Guid _tenant = ServiceFactory<ReportingAnalyticsService, ReportingAnalyticsDbContext>.DefaultTenantId;
    private readonly Guid _otherTenant = Guid.NewGuid();
    private readonly Guid _b1;
    private readonly Guid _b2;
    private readonly HttpClient _admin;
    private readonly HttpClient _otherAdmin;
    private readonly HttpClient _manager1;
    private readonly HttpClient _manager2;

    public ReportTests()
    {
        _b1 = _f.Branches.Add(_tenant, name: "Quận 1");
        _b2 = _f.Branches.Add(_tenant, name: "=Quận 3");
        _admin = _f.As(AicosRoles.BrandAdmin);
        _otherAdmin = _f.As(AicosRoles.BrandAdmin, tenantId: _otherTenant);
        _manager1 = _f.As(AicosRoles.BranchManager, _b1);
        _manager2 = _f.As(AicosRoles.BranchManager, _b2);
    }

    public void Dispose() => _f.Dispose();

    /// <summary>A UTC time on 2 November 2026 given as shop time (UTC+7).</summary>
    private static DateTime Local(int hour, int minute = 0, int dayOffset = 0) => new DateTime(2026, 11, 2, hour, minute, 0, DateTimeKind.Utc).AddHours(-7).AddDays(dayOffset);

    private async Task PlaceAsync(Guid order, Guid branch, decimal total, DateTime at, string type = "Takeaway", Guid? tenant = null) =>
        await _f.HandleAsync(new OrderCreatedIntegrationEvent(tenant ?? _tenant, order, "ORD-" + order.ToString()[..4], branch, null, null, null, null, type, total, false, null, [], at));

    private async Task PayAsync(Guid order, Guid branch, decimal amount, string method, DateTime at, Guid? tenant = null) =>
        await _f.HandleAsync(new PaymentSucceededEvent(tenant ?? _tenant, branch, Guid.NewGuid(), order, amount, method, at));

    private async Task SeedAsync()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var d = Guid.NewGuid();
        var e = Guid.NewGuid();
        await PlaceAsync(a, _b1, 100_000, Local(9, 30), "DineIn");
        await PayAsync(a, _b1, 100_000, "Cash", Local(9, 40));
        await PlaceAsync(b, _b1, 60_000, Local(10, 10));
        await PayAsync(b, _b1, 60_000, "Momo", Local(10, 11));
        await _f.HandleAsync(new PaymentRefundedEvent(_tenant, _b1, Guid.NewGuid(), Guid.NewGuid(), b, 20_000, 20_000, "Momo"));
        await PlaceAsync(c, _b1, 30_000, Local(10, 40));                                                       // still unpaid
        await PlaceAsync(d, _b1, 45_000, Local(10, 50));
        await _f.HandleAsync(new OrderCancelledIntegrationEvent(_tenant, d, _b1, null, "x", false, [], Local(10, 55)));   // never paid: not an order
        await PlaceAsync(e, _b2, 40_000, Local(15, 5));
        await PayAsync(e, _b2, 40_000, "VietQR", Local(15, 6));
    }

    // ------------------------------------------------------------------ chain
    [Fact]
    public async Task The_chain_report_compares_branches_by_orders_and_revenue()
    {
        await SeedAsync();

        var report = await _admin.GetFromJsonAsync<JsonElement>($"{Reports}/chain?{Day}");

        var totals = report.GetProperty("totals");
        (totals.GetProperty("orders").GetInt32(), totals.GetProperty("paidOrders").GetInt32()).Should().Be((4, 3));       // A, B, C and E; D was cancelled unpaid
        (totals.GetProperty("grossRevenue").GetDecimal(), totals.GetProperty("refunded").GetDecimal(), totals.GetProperty("netRevenue").GetDecimal()).Should().Be((200_000m, 20_000m, 180_000m));
        totals.GetProperty("averageOrderValue").GetDecimal().Should().Be(66_667);

        var branches = report.GetProperty("branches").EnumerateArray().ToList();
        branches.Should().HaveCount(2);
        branches[0].GetProperty("branchName").GetString().Should().Be("Quận 1");                                          // highest net revenue first
        (branches[0].GetProperty("orders").GetInt32(), branches[0].GetProperty("netRevenue").GetDecimal()).Should().Be((3, 140_000m));
        branches[0].GetProperty("revenueShare").GetDouble().Should().BeApproximately(77.8, 0.05);
        (branches[1].GetProperty("orders").GetInt32(), branches[1].GetProperty("netRevenue").GetDecimal()).Should().Be((1, 40_000m));
    }

    [Fact]
    public async Task Events_can_arrive_in_any_order_and_twice_and_a_brand_is_never_mixed_with_another()
    {
        var order = Guid.NewGuid();
        await PayAsync(order, _b1, 50_000, "Cash", Local(9, 1));                                                   // the payment arrives first
        await PlaceAsync(order, _b1, 50_000, Local(9, 0));
        await PlaceAsync(order, _b1, 50_000, Local(9, 0));
        await PayAsync(order, _b1, 50_000, "Cash", Local(9, 1));
        await PlaceAsync(order, _b1, 999_000, Local(9, 0), tenant: _otherTenant);                                  // an id clash from another brand is ignored
        await PlaceAsync(Guid.NewGuid(), _b1, 5_000, Local(9, 0), tenant: Guid.Empty);                             // the Guest QR flow has no brand yet

        var totals = (await _admin.GetFromJsonAsync<JsonElement>($"{Reports}/chain?{Day}")).GetProperty("totals");

        (totals.GetProperty("orders").GetInt32(), totals.GetProperty("grossRevenue").GetDecimal()).Should().Be((1, 50_000m));
        (await _otherAdmin.GetFromJsonAsync<JsonElement>($"{Reports}/chain?{Day}")).GetProperty("totals").GetProperty("orders").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Days_follow_the_shop_time_zone_not_utc()
    {
        await PlaceAsync(Guid.NewGuid(), _b1, 10_000, Local(23, 30));                                              // 16:30 UTC on the 2nd
        await PlaceAsync(Guid.NewGuid(), _b1, 10_000, Local(0, 30, dayOffset: 1));                                 // 17:30 UTC on the 2nd, but the 3rd in the shop

        (await _admin.GetFromJsonAsync<JsonElement>($"{Reports}/chain?{Day}")).GetProperty("totals").GetProperty("orders").GetInt32().Should().Be(1);
        (await _admin.GetFromJsonAsync<JsonElement>($"{Reports}/chain?from=2026-11-02&to=2026-11-03")).GetProperty("totals").GetProperty("orders").GetInt32().Should().Be(2);
    }

    // ------------------------------------------------------------------ branch
    [Fact]
    public async Task The_branch_report_shows_payment_methods_order_types_and_peak_hours()
    {
        await SeedAsync();

        var report = await _manager1.GetFromJsonAsync<JsonElement>($"{Reports}/branch?{Day}");

        report.GetProperty("branchName").GetString().Should().Be("Quận 1");
        var methods = report.GetProperty("paymentMethods").EnumerateArray().ToDictionary(m => m.GetProperty("method").GetString()!, m => m.GetProperty("netRevenue").GetDecimal());
        methods.Should().BeEquivalentTo(new Dictionary<string, decimal> { ["Cash"] = 100_000, ["Momo"] = 40_000 });
        var types = report.GetProperty("orderTypes").EnumerateArray().ToDictionary(t => t.GetProperty("orderType").GetString()!, t => t.GetProperty("orders").GetInt32());
        types.Should().BeEquivalentTo(new Dictionary<string, int> { ["DineIn"] = 1, ["Takeaway"] = 2 });

        var hours = report.GetProperty("hours").EnumerateArray().ToList();
        hours.Should().HaveCount(24);
        (hours[9].GetProperty("orders").GetInt32(), hours[10].GetProperty("orders").GetInt32(), hours[15].GetProperty("orders").GetInt32()).Should().Be((1, 2, 0));
        report.GetProperty("peakHours").EnumerateArray().Select(h => h.GetProperty("hour").GetInt32()).Should().Equal(10, 9);
        var day = report.GetProperty("days").EnumerateArray().Single();
        (day.GetProperty("orders").GetInt32(), day.GetProperty("grossRevenue").GetDecimal()).Should().Be((3, 160_000m));
    }

    [Fact]
    public async Task The_other_report_views_cover_the_chain_for_a_brand_admin_and_one_branch_for_a_manager()
    {
        await SeedAsync();

        var chainMethods = await _admin.GetFromJsonAsync<JsonElement>($"{Reports}/payment-methods?{Day}");
        chainMethods.EnumerateArray().Select(m => m.GetProperty("method").GetString()).Should().BeEquivalentTo("Cash", "Momo", "VietQR");
        chainMethods.EnumerateArray().Sum(m => m.GetProperty("share").GetDouble()).Should().BeApproximately(100, 0.2);
        (await _manager2.GetFromJsonAsync<JsonElement>($"{Reports}/payment-methods?{Day}")).EnumerateArray().Select(m => m.GetProperty("method").GetString()).Should().Equal("VietQR");
        (await _admin.GetFromJsonAsync<JsonElement>($"{Reports}/peak-hours?{Day}")).EnumerateArray().Sum(h => h.GetProperty("orders").GetInt32()).Should().Be(4);
        (await _admin.GetFromJsonAsync<JsonElement>($"{Reports}/peak-hours?{Day}&branchId={_b2}")).EnumerateArray().Sum(h => h.GetProperty("orders").GetInt32()).Should().Be(1);
    }

    // ------------------------------------------------------------------ access and input
    [Fact]
    public async Task Access_follows_roles_branches_and_brands()
    {
        await _manager1.GetAsync($"{Reports}/chain?{Day}").ShouldBe(403);                                         // the chain view is for the brand admin
        await _manager1.GetAsync($"{Reports}/branch?{Day}&branchId={_b2}").ShouldBe(403, "branch.forbidden");
        await _admin.GetAsync($"{Reports}/branch?{Day}").ShouldBe(400);                                           // a brand admin names the branch
        await _admin.GetAsync($"{Reports}/branch?{Day}&branchId={_b2}").ShouldBe(200);
        await _admin.GetAsync($"{Reports}/branch?{Day}&branchId={Guid.NewGuid()}").ShouldBe(400);
        await _otherAdmin.GetAsync($"{Reports}/branch?{Day}&branchId={_b1}").ShouldBe(400);                       // not their branch
        await _f.As(AicosRoles.Cashier, _b1).GetAsync($"{Reports}/branch?{Day}").ShouldBe(403);
        await _f.As(AicosRoles.Customer).GetAsync($"{Reports}/branch?{Day}").ShouldBe(403);
        await _f.As(AicosRoles.SuperAdmin).GetAsync($"{Reports}/chain?{Day}").ShouldBe(403);
        await _f.CreateClient().GetAsync($"{Reports}/chain?{Day}").ShouldBe(401);
    }

    [Fact]
    public async Task The_period_is_validated_and_defaults_to_today()
    {
        await _admin.GetAsync($"{Reports}/chain?from=2026-11-05&to=2026-11-01").ShouldBe(400);
        await _admin.GetAsync($"{Reports}/chain?from=2026-01-01&to=2026-12-31").ShouldBe(400);
        var today = await _admin.GetFromJsonAsync<JsonElement>($"{Reports}/chain");
        today.GetProperty("from").GetString().Should().Be(today.GetProperty("to").GetString());
    }

    [Fact]
    public async Task The_reports_can_be_exported_as_csv_that_cannot_run_a_formula()
    {
        await SeedAsync();

        var chain = await _admin.GetAsync($"{Reports}/chain/export?{Day}");
        var branch = await _manager1.GetAsync($"{Reports}/branch/export?{Day}");

        chain.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
        var lines = (await chain.Content.ReadAsStringAsync()).TrimStart('﻿').Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        lines[0].Should().StartWith("Branch,Orders,Paid orders");
        lines.Should().Contain("\"Quận 1\",3,2,160000,20000,140000,80000,77.8");
        lines.Should().Contain(l => l.StartsWith("\"'=Quận 3\""));                                                 // the leading = is neutralised
        lines[^1].Should().StartWith("TOTAL,4,3,200000,20000,180000");
        (await branch.Content.ReadAsStringAsync()).Should().Contain("Payment method,Orders").And.Contain("Cash").And.Contain("10:00,2");
        await _manager1.GetAsync($"{Reports}/chain/export?{Day}").ShouldBe(403);
    }
}
