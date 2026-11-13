using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Serving;
using AICOS.KitchenOperations.Api;
using AICOS.KitchenOperations.Api.Features.CupServing;
using AICOS.KitchenOperations.Api.Persistence;
using AICOS.Ordering.Api;
using AICOS.Ordering.Api.Domain;
using AICOS.Ordering.Api.Domain.Entities;
using AICOS.Ordering.Api.Domain.Enums;
using AICOS.Ordering.Api.Features.KitchenAdmission;
using AICOS.Ordering.Api.Persistence;
using AICOS.Ordering.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace AICOS.Services.IntegrationTests;

/// <summary>Real service-role TCP logins/SQL/HTTP, test-signed Staff JWTs and fake Table/branch directories. Not Mobile E2E.</summary>
[Collection("Staff operations infrastructure")]
public sealed class CupServingAclInfrastructureTests
{
    [OperationalInfrastructureFact]
    public async Task Nonowner_service_logins_support_custody_projection_and_deny_ledger_mutation()
    {
        var ordering = OperationalTestTarget.Required("AICOS_OPERATIONS_ORDERING_CONNECTION");
        var kitchen = OperationalTestTarget.Required("AICOS_OPERATIONS_KITCHEN_CONNECTION");
        var redis = OperationalTestTarget.Required("AICOS_OPERATIONS_REDIS");
        await OperationalTestTarget.VerifyAsync(ordering); await OperationalTestTarget.VerifyAsync(kitchen);
        await OperationalTestTarget.VerifyRedisAsync();
        // Reuse the reviewed schema/preflight path; each runner mode gets a fresh cluster.
        await using (var adminOrders = new ServiceFactory<OrderingService, OrderingDbContext>(QuietWorkers, ordering, operationalRedis: redis))
        {
            using var scope = adminOrders.Services.CreateScope();
            await CupServingInfrastructureTests.ExpandAsync(scope.ServiceProvider.GetRequiredService<OrderingDbContext>(), ordering,
                "ordering", "20261106164220_KitchenAdmissionOutbox", "02_ordering_expand.sql");
        }
        await using (var adminKitchen = new ServiceFactory<KitchenOperationsService, KitchenOperationsDbContext>(QuietWorkers, kitchen, operationalRedis: redis))
        {
            using var scope = adminKitchen.Services.CreateScope();
            await CupServingInfrastructureTests.ExpandAsync(scope.ServiceProvider.GetRequiredService<KitchenOperationsDbContext>(), kitchen,
                "kitchen", "20261106164227_KitchenAdmissionReceipts", "01_kitchen_expand.sql");
        }
        var roles = await CupServingAclFixture.CreateAsync(ordering, kitchen);
        await CupServingAclFixture.VerifyRoleAsync(roles.Kitchen, "kitchen_svc", "kitchen", "cup_operations");
        await CupServingAclFixture.VerifyRoleAsync(roles.Ordering, "ordering_svc", "ordering", "fulfillment_receipts");
        await DenyOtherDatabaseAsync(roles.Kitchen, new NpgsqlConnectionStringBuilder(ordering).Database!);
        await DenyOtherDatabaseAsync(roles.Ordering, new NpgsqlConnectionStringBuilder(kitchen).Database!);

        var settings = new Dictionary<string, string>
        {
            ["CupServing__AdmissionEnabled"] = "true",
            ["CupServing__ApiEnabled"] = "true",
            ["CupServing__LeasePolicyApproved"] = "true",
            ["CupServing__LeaseSeconds"] = "120", // explicit test fixture only
            ["CupServing__CaptureEnabled"] = "true",
            ["CupServing__ProjectionReceiverEnabled"] = "true",
            ["KitchenAdmission__MachineKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)),
            ["CupServing__MachineKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)),
        };
        var prior = settings.ToDictionary(p => p.Key, p => Environment.GetEnvironmentVariable(p.Key));
        foreach (var setting in settings) Environment.SetEnvironmentVariable(setting.Key, setting.Value);
        try
        {
            var tables = new FakeTableQr();
            await using var orders = new ServiceFactory<OrderingService, OrderingDbContext>(services =>
            {
                QuietWorkers(services); services.RemoveAll<ITableQrClient>(); services.AddSingleton<ITableQrClient>(tables);
            }, ordering, options => options.UseNpgsql(roles.Ordering), redis);
            await using var bar = new ServiceFactory<KitchenOperationsService, KitchenOperationsDbContext>(QuietWorkers,
                kitchen, options => options.UseNpgsql(roles.Kitchen), redis);
            var tenant = ServiceFactory<OrderingService, OrderingDbContext>.DefaultTenantId;
            var branch = orders.Branches.Add(tenant); bar.Branches.Add(tenant, id: branch);
            var sourceToken = tables.Add(tenant, branch, "S01"); var targetToken = tables.Add(tenant, branch, "S02");
            var source = (await tables.LookupAsync(sourceToken, default))!;
            Guid orderId, sessionId; AICOS.Contracts.Serving.CupAdmissionRequest admission;
            using (var scope = orders.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
                (await db.Database.SqlQuery<string>($"SELECT current_user AS \"Value\"").SingleAsync()).Should().Be("ordering_svc");
                var session = new TableSession(tenant, branch, source.TableId, source.TableCode, DateTime.UtcNow);
                var order = new Order(tenant, branch, OrderType.DineIn, DateTime.UtcNow, tableId: source.TableId, tableNumber: source.TableCode);
                order.AddItem(Guid.NewGuid(), "ACL test coffee", 35000, 2); order.AttachToTableSession(session.Id);
                db.TableSessions.Add(session); db.Orders.Add(order); db.Entry(order).Property(o => o.FulfillmentMode).CurrentValue = FulfillmentModes.CupV1;
                await db.SaveChangesAsync(); orderId = order.Id; sessionId = session.Id;
                admission = AdmissionProjection.CupRequest(await db.AdmissionOutbox.SingleAsync(), order);
            }
            // Production HTTP machine adapter with fresh credentials; no direct write to Kitchen tables.
            using var kitchenMachine = bar.CreateClient();
            var admissionTransport = new HttpKitchenAdmissionTransport(kitchenMachine, orders.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>());
            (await admissionTransport.SendCupAsync(admission, default)).Status.Should().Be(200);
            bar.Clock.Now = bar.Clock.Now.AddSeconds(1);
            (await admissionTransport.SendCupAsync(admission, default)).Status.Should().Be(200);
            Guid[] cupIds;
            using (var scope = bar.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<KitchenOperationsDbContext>();
                (await db.Database.SqlQuery<string>($"SELECT current_user AS \"Value\"").SingleAsync()).Should().Be("kitchen_svc");
                cupIds = await db.CupTasks.OrderBy(c => c.OrderCupIndex).Select(c => c.Id).ToArrayAsync();
            }
            using var barista = bar.As(AicosRoles.Barista, branch);
            using var staff1 = bar.As(AicosRoles.Cashier, branch, Guid.NewGuid());
            using var staff2 = bar.As(AicosRoles.Cashier, branch, Guid.NewGuid());
            foreach (var cup in cupIds)
            {
                await ActAsync(barista, cup, "start", "start-" + cup, 0).ShouldBe(200);
                await ActAsync(barista, cup, "ready", "ready-" + cup, 1).ShouldBe(200);
            }
            var claims = await Task.WhenAll(ActAsync(staff1, cupIds[0], "claim", "claim-a", 2), ActAsync(staff2, cupIds[1], "claim", "claim-b", 2));
            foreach (var claim in claims) claim.StatusCode.Should().Be(HttpStatusCode.OK, await claim.Content.ReadAsStringAsync());
            var claimBodies = await Task.WhenAll(claims.Select(claim => claim.Body()));
            using (var manager = bar.As(AicosRoles.BranchManager, branch, Guid.NewGuid()))
            {
                var ownerClaim = claimBodies[0].GetProperty("claim");
                await ActAsync(manager, cupIds[0], "pickup", "manager-cannot-override", 3,
                    ownerClaim.GetProperty("id").GetGuid(), 1).ShouldBe(403);
            }
            // Existing transfer must reject atomically, retaining the live claims and source table.
            using (var cashier = orders.As(AicosRoles.Cashier, branch))
            {
                await cashier.Post($"/api/v1/orders/qr/sessions/{sessionId}/transfer", new { expectedTableId = source.TableId, targetQrToken = targetToken })
                    .ShouldBe(409, "order.cup_serving_required");
                await cashier.Post($"/api/v1/orders/{orderId}/deliver", new { }).ShouldBe(409, "order.cup_serving_required");
                await cashier.Post($"/api/v1/orders/{orderId}/cancel", new { reason = "Legacy cancellation" }).ShouldBe(409, "order.cup_serving_required");
            }
            using (var scope = orders.Services.CreateScope())
                (await scope.ServiceProvider.GetRequiredService<OrderingDbContext>().TableSessions.SingleAsync()).TableId.Should().Be(source.TableId);
            for (var i = 0; i < cupIds.Length; i++)
            {
                var claim = claimBodies[i].GetProperty("claim");
                var actor = i == 0 ? staff1 : staff2; var claimId = claim.GetProperty("id").GetGuid();
                await ActAsync(actor, cupIds[i], "pickup", "pickup-" + i, 3, claimId, 1).ShouldBe(200);
                await ActAsync(actor, cupIds[i], "serve", "serve-" + i, 4, claimId, 1).ShouldBe(200);
                await ActAsync(actor, cupIds[i], "serve", "serve-" + i, 4, claimId, 1).ShouldBe(200);
            }
            using var orderingMachine = orders.CreateClient();
            var projection = new HttpFulfillmentTransport(orderingMachine, orders.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>());
            using (var scope = bar.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<KitchenOperationsDbContext>();
                (await new FulfillmentDispatcher(db, projection).DispatchOnceAsync()).Should().Be(10);
                var ticket = await db.Tickets.SingleAsync(); ticket.Status.ToString().Should().Be("Completed");
                ticket.IsPaid.Should().BeFalse(); ticket.SourceTableId.Should().Be(source.TableId);
                ticket.TableNumber.Should().Be(source.TableCode);
                (await db.CupOperations.CountAsync()).Should().Be(10); (await db.CupClaims.CountAsync()).Should().Be(2);
                (await db.CupStickers.CountAsync()).Should().Be(2);
                var latest = await db.FulfillmentOutbox.OrderByDescending(r => r.SourceVersion).Select(r => r.Payload).FirstAsync();
                (await projection.SendAsync(JsonSerializer.Deserialize<FulfillmentSnapshot>(latest)!, default)).Status.Should().Be(200);
            }
            using (var scope = orders.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
                var summary = await db.FulfillmentProjection.SingleAsync(); summary.Served.Should().Be(2); summary.FulfilledAtUtc.Should().NotBeNull();
                (await db.FulfillmentReceipts.CountAsync()).Should().Be(10);
                (await db.Orders.SingleAsync()).PaymentStatus.Should().Be(PaymentStatus.Unpaid);
                (await db.TableSessions.SingleAsync()).IsOpen.Should().BeTrue();
            }
            await CupServingAclFixture.VerifyRoleAsync(roles.Kitchen, "kitchen_svc", "kitchen", "cup_operations");
            await CupServingAclFixture.VerifyRoleAsync(roles.Ordering, "ordering_svc", "ordering", "fulfillment_receipts");
            await roles.WriteMatrixAsync();
        }
        finally { foreach (var setting in prior) Environment.SetEnvironmentVariable(setting.Key, setting.Value); }
    }

    private static void QuietWorkers(IServiceCollection services)
    {
        foreach (var worker in services.Where(s => s.ImplementationType?.Name is "KitchenAdmissionWorker" or "FulfillmentWorker"
            or "TableSessionExpiryWorker" or "PickupOrderExpiryWorker").ToArray()) services.Remove(worker);
    }

    private static Task<HttpResponseMessage> ActAsync(HttpClient client, Guid cup, string action, string key, long version, Guid? claim = null, long? fence = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/kitchen/cups/{cup}/{action}")
        { Content = JsonContent.Create(new { expectedVersion = version, expectedCycle = 1, claimId = claim, fence }) };
        request.Headers.Add("Idempotency-Key", key); return client.SendAsync(request);
    }

    private static async Task DenyOtherDatabaseAsync(string connection, string other)
    {
        var target = new NpgsqlConnectionStringBuilder(connection) { Database = other, Pooling = false };
        await using var probe = new NpgsqlConnection(target.ConnectionString);
        var failure = await Assert.ThrowsAsync<PostgresException>(() => probe.OpenAsync());
        failure.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }
}
