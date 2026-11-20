using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.Ordering.Api;
using AICOS.Ordering.Api.Domain;
using AICOS.Ordering.Api.Domain.Entities;
using AICOS.Ordering.Api.Domain.Enums;
using AICOS.Ordering.Api.Persistence;
using AICOS.Ordering.Api.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AICOS.Services.IntegrationTests;

public sealed class FirstTableOrderTests : IDisposable
{
    private readonly FakeMenu _menu = new();
    private readonly FakeTableQr _tables = new();
    private readonly ServiceFactory<OrderingService, OrderingDbContext> _factory;
    private readonly Guid _tenant = ServiceFactory<OrderingService, OrderingDbContext>.DefaultTenantId;
    private readonly Guid _branch;

    public FirstTableOrderTests()
    {
        _factory = new(services =>
        {
            services.RemoveAll<IMenuCatalogClient>(); services.AddSingleton<IMenuCatalogClient>(_menu);
            services.RemoveAll<ITableQrClient>(); services.AddSingleton<ITableQrClient>(_tables);
        }, settings: new Dictionary<string, string?> { ["SessionBilling:Enabled"] = "true" });
        _branch = _factory.Branches.Add(_tenant);
    }

    [Fact]
    public async Task First_counter_order_creates_session_atomically_then_replay_and_add_round_reuse_it()
    {
        var qr = _tables.Add(_tenant, _branch, "EMPTY01");
        var table = (await _tables.LookupAsync(qr, default))!;
        var product = _menu.Add(_branch, "Coffee", 50000);
        using var client = _factory.As(AicosRoles.Cashier, _branch);
        client.DefaultRequestHeaders.Add("Idempotency-Key", "first-table-order");
        var body = new { branchId = _branch, type = "DineIn", tableId = table.TableId,
            tableNumber = table.TableCode, tableQrToken = qr, items = new[] { new { productId = product, quantity = 1 } } };
        var first = await client.PostAsJsonAsync("/api/v1/orders", body);
        first.StatusCode.Should().Be(HttpStatusCode.Created, await first.Content.ReadAsStringAsync());
        var order = await first.Content.ReadFromJsonAsync<JsonElement>();
        var replay = await client.PostOk("/api/v1/orders", body);
        replay.GetProperty("id").GetGuid().Should().Be(order.GetProperty("id").GetGuid());
        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        client.DefaultRequestHeaders.Add("Idempotency-Key", "second-table-order");
        var next = await client.PostOk("/api/v1/orders", body);
        next.GetProperty("tableSessionId").GetGuid().Should().Be(order.GetProperty("tableSessionId").GetGuid());
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        db.TableSessions.Count().Should().Be(1); db.Orders.Count().Should().Be(2);
    }

    [Theory]
    [InlineData("closed")]
    [InlineData("missing")]
    [InlineData("wrong-table")]
    [InlineData("wrong-branch")]
    public async Task Invalid_session_is_still_rejected(string kind)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        var table = Guid.NewGuid();
        var session = new TableSession(_tenant, kind == "wrong-branch" ? Guid.NewGuid() : _branch,
            kind == "wrong-table" ? Guid.NewGuid() : table, "T01", DateTime.UtcNow);
        if (kind == "closed") { session.Close(TableSessionCloseReason.Staff, DateTime.UtcNow); db.TableSessions.Add(session); await db.SaveChangesAsync(); }
        if (kind is "wrong-table" or "wrong-branch") db.TableSessions.Add(session);
        var order = new Order(_tenant, _branch, OrderType.DineIn, DateTime.UtcNow, tableId: table, tableNumber: "T01");
        order.AttachToTableSession(session.Id); db.Orders.Add(order);
        var save = async () => await db.SaveChangesAsync();
        await save.Should().ThrowAsync<ConflictException>();
        using var verify = _factory.Services.CreateScope();
        verify.ServiceProvider.GetRequiredService<OrderingDbContext>().Orders.Count().Should().Be(0);
    }

    public void Dispose() => _factory.Dispose();
}
