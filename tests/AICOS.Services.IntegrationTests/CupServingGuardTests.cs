using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;
using AICOS.Contracts.Serving;
using AICOS.KitchenOperations.Api;
using AICOS.KitchenOperations.Api.Domain.Entities;
using AICOS.KitchenOperations.Api.Persistence;
using AICOS.Ordering.Api;
using AICOS.Ordering.Api.Domain.Entities;
using AICOS.Ordering.Api.Domain.Enums;
using AICOS.Ordering.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;

namespace AICOS.Services.IntegrationTests;

/// <summary>HTTP TestServer + InMemory, and offline EF metadata. Not real PostgreSQL concurrency.</summary>
public sealed class CupServingGuardTests
{
    [Fact]
    public async Task Cup_routes_and_capabilities_remain_disabled_by_default()
    {
        using var f = new ServiceFactory<KitchenOperationsService, KitchenOperationsDbContext>();
        var branch = f.Branches.Add(ServiceFactory<KitchenOperationsService, KitchenOperationsDbContext>.DefaultTenantId);
        using var staff = f.As(AicosRoles.Cashier, branch);
        var capabilities = await staff.GetFromJsonAsync<JsonElement>("/api/v1/kitchen/staff/capabilities");
        capabilities.GetProperty("cupServingVersion").GetInt32().Should().Be(0);
        capabilities.GetProperty("claimVersion").GetInt32().Should().Be(0);
        (await staff.GetAsync("/api/v1/kitchen/cups?orderId=" + Guid.NewGuid())).StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Legacy_delivery_and_cancel_cannot_bypass_cup_mode_and_do_not_publish_effects()
    {
        using var f = new ServiceFactory<OrderingService, OrderingDbContext>();
        var tenant = ServiceFactory<OrderingService, OrderingDbContext>.DefaultTenantId;
        var branch = f.Branches.Add(tenant); Guid id;
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            var order = new Order(tenant, branch, OrderType.DineIn, DateTime.UtcNow);
            order.AddItem(Guid.NewGuid(), "Coffee", 50000, 2); order.MarkAsReady(DateTime.UtcNow);
            db.Orders.Add(order); db.Entry(order).Property(o => o.FulfillmentMode).CurrentValue = FulfillmentModes.CupV1;
            await db.SaveChangesAsync(); id = order.Id;
        }
        using var staff = f.As(AicosRoles.Cashier, branch);
        var read = await staff.GetFromJsonAsync<JsonElement>($"/api/v1/orders/{id}");
        read.GetProperty("fulfillmentMode").GetString().Should().Be(FulfillmentModes.CupV1);
        await staff.Post($"/api/v1/orders/{id}/deliver", new { }).ShouldBe(409, "order.cup_serving_required");
        await staff.Post($"/api/v1/orders/{id}/cancel", new { reason = "Cannot bypass" }).ShouldBe(409, "order.cup_serving_required");
        using var verify = f.Services.CreateScope();
        var saved = await verify.ServiceProvider.GetRequiredService<OrderingDbContext>().Orders.SingleAsync(o => o.Id == id);
        saved.DeliveredAtUtc.Should().BeNull(); saved.PaymentStatus.Should().Be(PaymentStatus.Unpaid);
        saved.Status.Should().Be(OrderStatus.Ready); f.Events.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task Whole_ticket_status_stickers_and_legacy_delivery_fail_closed_for_cup_mode()
    {
        using var f = new ServiceFactory<KitchenOperationsService, KitchenOperationsDbContext>();
        var tenant = ServiceFactory<KitchenOperationsService, KitchenOperationsDbContext>.DefaultTenantId;
        var branch = f.Branches.Add(tenant); Guid id; var order = Guid.NewGuid();
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KitchenOperationsDbContext>();
            var ticket = new KitchenTicket(tenant, order, "CUP-GUARD", branch, "S01", "DineIn", false);
            ticket.AddItem(Guid.NewGuid(), "Coffee", 2);
            db.Tickets.Add(ticket); db.Entry(ticket).Property(t => t.FulfillmentMode).CurrentValue = FulfillmentModes.CupV1;
            await db.SaveChangesAsync(); id = ticket.Id;
        }
        using var barista = f.As(AicosRoles.Barista, branch);
        var capabilities = await barista.GetFromJsonAsync<JsonElement>("/api/v1/kitchen/staff/capabilities");
        capabilities.GetProperty("cupServingVersion").GetInt32().Should().Be(0);
        await barista.Put($"/api/v1/kitchen/tickets/{id}/status", new { status = "Brewing" }).ShouldBe(409, "order.cup_serving_required");
        await barista.GetAsync($"/api/v1/kitchen/tickets/{id}/stickers").ShouldBe(409, "order.cup_serving_required");
        await barista.GetAsync($"/api/v1/kitchen/tickets/{id}/sticker-preview").ShouldBe(409, "order.cup_serving_required");
        (await barista.GetFromJsonAsync<JsonElement>("/api/v1/kitchen/queue")).GetArrayLength().Should().Be(0);
        await f.HandleAsync(new OrderDeliveredIntegrationEvent(tenant, branch, order, null, Guid.NewGuid(), DateTime.UtcNow));
        using var verify = f.Services.CreateScope();
        var saved = await verify.ServiceProvider.GetRequiredService<KitchenOperationsDbContext>().Tickets.SingleAsync(t => t.Id == id);
        saved.Status.ToString().Should().Be("Pending");
        saved.RecoveryHoldReason.Should().Be("cup.legacy_event_requires_review");
    }

    [Fact]
    public void Foundation_migrations_only_expand_owned_schemas_without_recreating_existing_ticket_index()
    {
        using var kitchen = new KitchenOperationsDbContext(new DbContextOptionsBuilder<KitchenOperationsDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=metadata_only;Username=unused;Password=unused").UseSnakeCaseNamingConvention().Options);
        using var orders = new OrderingDbContext(new DbContextOptionsBuilder<OrderingDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=metadata_only;Username=unused;Password=unused").UseSnakeCaseNamingConvention().Options);
        CheckMigration(kitchen, "KitchenCupServingFoundation", "kitchen", 4);
        CheckMigration(orders, "OrderingCupFulfillmentFoundation", "ordering", 2);
    }

    private static void CheckMigration(DbContext db, string name, string schema, int tables)
    {
        var assembly = db.GetService<IMigrationsAssembly>();
        var type = assembly.Migrations.Single(m => m.Key.EndsWith(name, StringComparison.Ordinal)).Value;
        var operations = assembly.CreateMigration(type, db.Database.ProviderName!).UpOperations;
        var allowed = new[] { typeof(AddColumnOperation), typeof(CreateTableOperation), typeof(CreateIndexOperation),
            typeof(AddForeignKeyOperation), typeof(AddCheckConstraintOperation) };
        operations.Should().OnlyContain(op => allowed.Contains(op.GetType()));
        operations.OfType<CreateTableOperation>().Should().HaveCount(tables).And.OnlyContain(t => t.Schema == schema);
        foreach (var table in operations.OfType<CreateTableOperation>())
            table.ForeignKeys.All(fk => fk.PrincipalSchema == schema).Should().BeTrue();
        operations.OfType<CreateIndexOperation>().Should().NotContain(i => i.Name == "ix_kitchen_tickets_tenant_id_order_id");
    }
}
