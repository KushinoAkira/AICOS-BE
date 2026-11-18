using System.Text.Json;
using AICOS.Contracts.Serving;
using AICOS.Contracts.Billing;
using AICOS.KitchenOperations.Api.Features.CupServing;
using AICOS.KitchenOperations.Api.Persistence;
using AICOS.Ordering.Api.Domain;
using AICOS.Ordering.Api.Domain.Entities;
using AICOS.Ordering.Api.Domain.Enums;
using AICOS.Ordering.Api.Persistence;
using AICOS.Payments.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

// Test tool only: not an API, startup hook or operating migration command.
internal static class ItemTransferDesignProbe
{
    private static DbContextOptions<T> Options<T>(string connection) where T : DbContext
    {
        var schema = typeof(T) == typeof(OrderingDbContext) ? "ordering"
            : typeof(T) == typeof(KitchenOperationsDbContext) ? "kitchen" : "payment";
        return new DbContextOptionsBuilder<T>().UseNpgsql(connection,
            postgres => postgres.MigrationsHistoryTable("__ef_migrations_history", schema))
            .UseSnakeCaseNamingConvention().Options;
    }

    public static async Task<bool> TryRunAsync(string[] args)
    {
        if (args is ["item-transfer-scripts", var folder])
        {
            Directory.CreateDirectory(folder);
            const string offline = "Host=127.0.0.1;Port=1;Database=offline;Username=offline";
            using var ordering = new OrderingDbContext(Options<OrderingDbContext>(offline));
            using var kitchen = new KitchenOperationsDbContext(Options<KitchenOperationsDbContext>(offline));
            using var payments = new PaymentsDbContext(Options<PaymentsDbContext>(offline));
            foreach (var (name, db) in new (string, DbContext)[] { ("ordering", ordering), ("kitchen", kitchen), ("payment", payments) })
                File.WriteAllText(Path.Combine(folder, name + ".sql"), db.GetService<IMigrator>().GenerateScript());
            Console.WriteLine("Offline current EF migration scripts exported; no DB connection.");
            return true;
        }
        if (args is not ["item-transfer-fixture"] and not ["item-transfer-replay"]) return false;
        var connections = new Dictionary<string, string>();
        foreach (var name in new[] { "ordering", "kitchen", "payment" })
        {
            var connection = Environment.GetEnvironmentVariable("DESIGN_" + name.ToUpperInvariant()) ?? throw new InvalidOperationException("Missing isolated connection.");
            var parsed = new NpgsqlConnectionStringBuilder(connection);
            if (parsed.Host != "postgres" || parsed.Database != "aicos_item_transfer_design_" + name)
                throw new InvalidOperationException("Refusing non-design database.");
            await using var probe = new NpgsqlConnection(connection);
            await probe.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT system_identifier::text FROM pg_control_system()", probe);
            if ((string?)await command.ExecuteScalarAsync() != Environment.GetEnvironmentVariable("DESIGN_CLUSTER"))
                throw new InvalidOperationException("Design cluster identity differs.");
            connections[name] = connection;
        }
        if (args[0] == "item-transfer-replay")
        {
            using var db = new KitchenOperationsDbContext(Options<KitchenOperationsDbContext>(connections["kitchen"]));
            var data = JsonSerializer.Deserialize<JsonElement>(Environment.GetEnvironmentVariable("DESIGN_FIXTURE")!);
            var replay = new CupAdmissionWriter(db, TimeProvider.System);
            foreach (var raw in data.GetProperty("admissions").EnumerateArray())
            {
                var request = raw.Deserialize<CupAdmissionRequest>()!;
                await replay.ApplyAsync(request);
                var changed = request.Manifest with { SessionId = Guid.NewGuid() };
                try
                {
                    await replay.ApplyAsync(request with { EventId = Guid.NewGuid(), Manifest = changed,
                        ManifestHash = CupAdmissionProtocol.Hash(request.TenantId, request.BranchId, request.OrderId, changed) });
                    throw new InvalidOperationException("Changed admission origin was accepted.");
                }
                catch (AICOS.BuildingBlocks.Exceptions.ConflictException) { db.ChangeTracker.Clear(); }
            }
            Console.WriteLine("Actual CupAdmissionWriter replay retained immutable source; changed source rejected.");
            return true;
        }
        var now = DateTime.UtcNow;
        var tenant = Guid.NewGuid(); var branch = Guid.NewGuid(); var actor = Guid.NewGuid();
        using var orders = new OrderingDbContext(Options<OrderingDbContext>(connections["ordering"]));
        using var kitchenDb = new KitchenOperationsDbContext(Options<KitchenOperationsDbContext>(connections["kitchen"]));
        var source = new TableSession(tenant, branch, Guid.NewGuid(), "D01", now);
        var target = new TableSession(tenant, branch, Guid.NewGuid(), "D02", now);
        orders.TableSessions.AddRange(source, target);
        var sourceOrder = new Order(tenant, branch, OrderType.DineIn, now, cashierId: actor, tableId: source.TableId, tableNumber: source.TableCode);
        sourceOrder.AttachToTableSession(source.Id); sourceOrder.AddItem(Guid.NewGuid(), "Design coffee including options", 50000, 3);
        var targetOrder = new Order(tenant, branch, OrderType.DineIn, now, cashierId: actor, tableId: target.TableId, tableNumber: target.TableCode);
        targetOrder.AttachToTableSession(target.Id); targetOrder.AddItem(Guid.NewGuid(), "Design tea", 40000, 1);
        orders.Orders.AddRange(sourceOrder, targetOrder);
        orders.Entry(sourceOrder).Property(x => x.FulfillmentMode).CurrentValue = FulfillmentModes.CupV1;
        orders.Entry(targetOrder).Property(x => x.FulfillmentMode).CurrentValue = FulfillmentModes.CupV1;
        await orders.SaveChangesAsync();
        var writer = new CupAdmissionWriter(kitchenDb, TimeProvider.System);
        var admissions = new List<object>();
        foreach (var (order, session) in new[] { (sourceOrder, source), (targetOrder, target) })
        {
            var manifest = new CupManifest(order.OrderCode, FulfillmentModes.CupV1, session.TableId, session.Id,
                order.Items.Select(i => new CupAdmissionItem(i.Id, i.ProductId, i.ProductName, i.Quantity, i.Note)).ToArray());
            var request = new CupAdmissionRequest(Guid.NewGuid(), tenant, branch, order.Id, manifest,
                CupAdmissionProtocol.Hash(tenant, branch, order.Id, manifest),
                new("DineIn", "Pending", false, session.TableCode, now, null, null, null, false));
            var ack = await writer.ApplyAsync(request);
            if ((await writer.ApplyAsync(request)).TicketId != ack.TicketId) throw new InvalidOperationException("Admission replay differs.");
            admissions.Add(request);
        }
        var sourceBill = BillIdentity.For(tenant, source.Id); var targetBill = BillIdentity.For(tenant, target.Id);
        Console.WriteLine(JsonSerializer.Serialize(new { tenant, branch, actor, sourceSession = source.Id, targetSession = target.Id,
            sourceTable = source.TableId, targetTable = target.TableId, sourceOrder = sourceOrder.Id, targetOrder = targetOrder.Id, sourceBill, targetBill,
            admissions, units = await kitchenDb.CupTasks.AsNoTracking().OrderBy(x => x.OrderId).ThenBy(x => x.UnitIndex)
                .Select(x => new { x.Id, x.OrderId, x.OrderItemId, x.UnitIndex, x.TicketId }).ToArrayAsync() }));
        return true;
    }
}
