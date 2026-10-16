using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AICOS.CustomerEngagement.Api;
using AICOS.IdentityOrganization.Api;
using AICOS.InventoryPurchasing.Api;
using AICOS.KitchenOperations.Api;
using AICOS.MenuCatalog.Api;
using AICOS.Ordering.Api;
using AICOS.Payments.Api;
using AICOS.ReportingAnalytics.Api;
using AICOS.TableReservation.Api;
using AICOS.WorkforceScheduling.Api;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AICOS.Services.SmokeTests;

/// <summary>
/// Boots every microservice in-memory (no PostgreSQL / Redis needed) and checks that the shared pipeline is wired:
/// DI container builds, options validate, routing prefix matches the gateway, health endpoint answers.
/// </summary>
public class ServiceSmokeTests
{
    [Fact] public Task Identity_boots() => AssertBoots<IdentityOrganizationService>("identity", "identity");
    [Fact] public Task Menu_boots() => AssertBoots<MenuCatalogService>("menu", "menu");
    [Fact] public Task Table_boots() => AssertBoots<TableReservationService>("table", "tables");
    [Fact] public Task Order_boots() => AssertBoots<OrderingService>("order", "orders");
    [Fact] public Task Payment_boots() => AssertBoots<PaymentsService>("payment", "payments");
    [Fact] public Task Inventory_boots() => AssertBoots<InventoryPurchasingService>("inventory", "inventory");
    [Fact] public Task Kitchen_boots() => AssertBoots<KitchenOperationsService>("kitchen", "kitchen");
    [Fact] public Task Workforce_boots() => AssertBoots<WorkforceSchedulingService>("workforce", "workforce");
    [Fact] public Task Reporting_boots() => AssertBoots<ReportingAnalyticsService>("reporting", "reporting");
    [Fact] public Task Customer_boots() => AssertBoots<CustomerEngagementService>("customer", "customers");

    private static async Task AssertBoots<TService>(string name, string routePrefix)
        where TService : class
    {
        await using var factory = new WebApplicationFactory<TService>();
        using var client = factory.CreateClient();

        var info = await client.GetFromJsonAsync<JsonElement>($"/api/v1/{routePrefix}/_info");
        info.GetProperty("service").GetString().Should().Be(name);

        var live = await client.GetAsync("/health/live");
        live.StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await client.GetAsync($"/api/v1/{routePrefix}/_info");
        response.Headers.Should().ContainKey("X-Correlation-Id");
    }
}
