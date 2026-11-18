using System.Net;
using System.Net.Http.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Ordering.Api;
using AICOS.Ordering.Api.Domain.Entities;
using AICOS.Ordering.Api.Domain.Enums;
using AICOS.Ordering.Api.Features.Orders;
using AICOS.Ordering.Api.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AICOS.Services.IntegrationTests;

public sealed class TakeawaySummaryTests : IDisposable
{
    private readonly ServiceFactory<OrderingService, OrderingDbContext> _factory = new();
    private readonly Guid _tenant = ServiceFactory<OrderingService, OrderingDbContext>.DefaultTenantId;
    private readonly Guid _branch = Guid.NewGuid();
    private string Path => $"/api/v1/orders/staff/takeaway-summary?branchId={_branch}";

    [Fact]
    public async Task Summary_counts_all_active_orders_without_pagination_or_side_effects()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        var now = DateTime.UtcNow;
        for (var i = 0; i < 35; i++) db.Orders.Add(new Order(_tenant, _branch, OrderType.Takeaway, now));
        var ready = new Order(_tenant, _branch, OrderType.Takeaway, now); ready.MarkAsReady(now); db.Orders.Add(ready);
        var delivered = new Order(_tenant, _branch, OrderType.Takeaway, now); delivered.MarkAsReady(now);
        delivered.Deliver(Guid.NewGuid(), now); db.Orders.Add(delivered);
        var cancelled = new Order(_tenant, _branch, OrderType.Takeaway, now); cancelled.CancelOrder("Fixture", now); db.Orders.Add(cancelled);
        db.Orders.Add(new Order(_tenant, _branch, OrderType.DineIn, now));
        db.Orders.Add(new Order(Guid.NewGuid(), _branch, OrderType.Takeaway, now));
        db.Orders.Add(new Order(_tenant, Guid.NewGuid(), OrderType.Takeaway, now));
        await db.SaveChangesAsync();
        var beforeEvents = _factory.Events.Published.Count;
        using var cashier = _factory.As(AicosRoles.Cashier, _branch);
        var response = await cashier.GetFromJsonAsync<TakeawaySummaryResponse>(Path);
        response!.ActiveOrders.Should().Be(36);
        response.ReadyOrders.Should().Be(1);
        response.BranchId.Should().Be(_branch);
        db.Orders.Count().Should().Be(41);
        _factory.Events.Published.Count.Should().Be(beforeEvents);
    }

    [Fact]
    public async Task Staff_cannot_read_another_branch_and_anonymous_is_rejected()
    {
        using var staff = _factory.As(AicosRoles.Cashier, Guid.NewGuid());
        (await staff.GetAsync(Path)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var anonymous = _factory.CreateClient();
        (await anonymous.GetAsync(Path)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Empty_branch_and_barista_read_are_supported()
    {
        using var barista = _factory.As(AicosRoles.Barista, _branch);
        var response = await barista.GetFromJsonAsync<TakeawaySummaryResponse>(Path);
        response!.ActiveOrders.Should().Be(0);
        response.ReadyOrders.Should().Be(0);
        (await barista.GetAsync("/api/v1/orders/staff/takeaway-summary?branchId=" + Guid.Empty)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Cup_readiness_is_not_guessed_from_legacy_order_status()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        var cupOrder = new Order(_tenant, _branch, OrderType.Takeaway, DateTime.UtcNow);
        db.Orders.Add(cupOrder);
        db.Entry(cupOrder).Property(o => o.FulfillmentMode).CurrentValue = AICOS.Contracts.Serving.FulfillmentModes.CupV1;
        await db.SaveChangesAsync();
        using var cashier = _factory.As(AicosRoles.Cashier, _branch);
        var response = await cashier.GetFromJsonAsync<TakeawaySummaryResponse>(Path);
        response!.ActiveOrders.Should().Be(1);
        response.ReadyOrdersKnown.Should().BeFalse();
    }

    public void Dispose() => _factory.Dispose();
}
