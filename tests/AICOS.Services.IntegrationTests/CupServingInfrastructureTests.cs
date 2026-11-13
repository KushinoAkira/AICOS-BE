using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.KitchenAdmission;
using AICOS.Contracts.Serving;
using AICOS.KitchenOperations.Api;
using AICOS.KitchenOperations.Api.Features.CupServing;
using AICOS.KitchenOperations.Api.Persistence;
using AICOS.Ordering.Api;
using AICOS.Ordering.Api.Domain.Entities;
using AICOS.Ordering.Api.Domain.Enums;
using AICOS.Ordering.Api.Features.Fulfillment;
using AICOS.Ordering.Api.Features.KitchenAdmission;
using AICOS.Ordering.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AICOS.Services.IntegrationTests;

/// <summary>Guarded real PostgreSQL migration/HTTP tests. Staff JWTs are test-signed; Identity is not called.</summary>
[Collection("Staff operations infrastructure")]
public sealed class CupServingInfrastructureTests
{
    [OperationalInfrastructureFact]
    public async Task Admission_claim_race_fencing_replay_projection_and_legacy_guards()
    {
        var ordering = OperationalTestTarget.Required("AICOS_OPERATIONS_ORDERING_CONNECTION");
        var kitchen = OperationalTestTarget.Required("AICOS_OPERATIONS_KITCHEN_CONNECTION");
        var redis = OperationalTestTarget.Required("AICOS_OPERATIONS_REDIS");
        await OperationalTestTarget.VerifyAsync(ordering); await OperationalTestTarget.VerifyAsync(kitchen);
        await OperationalTestTarget.VerifyRedisAsync();
        var settings = new Dictionary<string, string>
        {
            ["CupServing__AdmissionEnabled"] = "true",
            ["CupServing__ApiEnabled"] = "true",
            ["CupServing__LeasePolicyApproved"] = "true",
            ["CupServing__LeaseSeconds"] = "120", // test policy only
            ["CupServing__ProjectionReceiverEnabled"] = "true",
            ["CupServing__CaptureEnabled"] = "true",
            ["CupServing__AdmissionDispatchEnabled"] = "true",
            ["KitchenAdmission__MachineKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)),
            ["CupServing__MachineKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)),
        };
        var prior = settings.ToDictionary(s => s.Key, s => Environment.GetEnvironmentVariable(s.Key));
        foreach (var pair in settings) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        try
        {
            await using var orders = new ServiceFactory<OrderingService, OrderingDbContext>(services =>
            {
                // Manual worker ticks below use real SQL/HTTP with explicit fault injection.
                foreach (var worker in services.Where(s => s.ImplementationType?.Name == "KitchenAdmissionWorker").ToArray()) services.Remove(worker);
            }, postgresConnection: ordering, operationalRedis: redis);
            await using var bar = new ServiceFactory<KitchenOperationsService, KitchenOperationsDbContext>(postgresConnection: kitchen, operationalRedis: redis);
            using (var scope = orders.Services.CreateScope())
                await ExpandAsync(scope.ServiceProvider.GetRequiredService<OrderingDbContext>(), ordering, "ordering", "20261106164220_KitchenAdmissionOutbox", "02_ordering_expand.sql");
            using (var scope = bar.Services.CreateScope())
                await ExpandAsync(scope.ServiceProvider.GetRequiredService<KitchenOperationsDbContext>(), kitchen, "kitchen", "20261106164227_KitchenAdmissionReceipts", "01_kitchen_expand.sql");
            var tenant = ServiceFactory<OrderingService, OrderingDbContext>.DefaultTenantId;
            var branch = orders.Branches.Add(tenant); bar.Branches.Add(tenant, id: branch);
            AdmissionOutbox row; CupAdmissionRequest admission; Guid orderId;
            using (var scope = orders.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
                var order = new Order(tenant, branch, OrderType.DineIn, DateTime.UtcNow);
                order.AddItem(Guid.NewGuid(), "Coffee", 35000, 2);
                db.Orders.Add(order); db.Entry(order).Property(o => o.FulfillmentMode).CurrentValue = FulfillmentModes.CupV1;
                await db.SaveChangesAsync(); orderId = order.Id;
                row = await db.AdmissionOutbox.SingleAsync(); admission = AdmissionProjection.CupRequest(row, order);
                await scope.ServiceProvider.GetRequiredService<AICOS.Ordering.Api.Features.Orders.OrderEventPublisher>().CreatedAsync(order, default);
            }
            using var machine = bar.CreateClient();
            var denied = await machine.PostAsJsonAsync("/internal/v2/kitchen/order-admissions", admission, AdmissionProtocol.Json);
            denied.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            machine.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-token");
            (await machine.PostAsJsonAsync("/internal/v2/kitchen/order-admissions", admission, AdmissionProtocol.Json)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            machine.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdmissionMachineAuthentication.Token(
                settings["KitchenAdmission__MachineKey"], tenant, branch, "kitchen.admission.v2.write", DateTime.UtcNow));
            var admissionTransport = new AdmissionBridge(new HttpKitchenAdmissionTransport(machine, orders.Services.GetRequiredService<IConfiguration>()));
            using (var scope = orders.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
                var dispatcher = ActivatorUtilities.CreateInstance<KitchenAdmissionDispatcher>(scope.ServiceProvider, admissionTransport);
                (await dispatcher.DispatchOnceAsync()).Should().Be(0); // Kitchen unavailable after source commit
                (await db.AdmissionOutbox.AsNoTracking().SingleAsync()).State.Should().Be("PENDING");
                admissionTransport.Offline = false;
                await db.AdmissionOutbox.ExecuteUpdateAsync(s => s.SetProperty(r => r.NextAttemptAtUtc, DateTime.UtcNow.AddMinutes(-1)));
                (await dispatcher.DispatchOnceAsync()).Should().Be(1);
                var reconciler = ActivatorUtilities.CreateInstance<KitchenAdmissionReconciler>(scope.ServiceProvider, admissionTransport);
                (await reconciler.ReconcileOnceAsync(cups: true)).Should().Be(1);
                (await db.AdmissionReconcile.AsNoTracking().SingleAsync()).ConsumerKey.Should().Be(CupAdmissionProtocol.Consumer);
            }
            var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => machine.PostAsJsonAsync(
                "/internal/v2/kitchen/order-admissions", admission, AdmissionProtocol.Json)));
            foreach (var response in responses) response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            var crashRequest = admission with { EventId = Guid.NewGuid(), OrderId = Guid.NewGuid() };
            crashRequest = crashRequest with { ManifestHash = CupAdmissionProtocol.Hash(tenant, branch, crashRequest.OrderId, crashRequest.Manifest) };
            await KillProbeAsync("cup-before-commit", crashRequest, crashRequest.OrderId);
            using (var scope = bar.Services.CreateScope())
                (await scope.ServiceProvider.GetRequiredService<KitchenOperationsDbContext>().Tickets.CountAsync(t => t.OrderId == crashRequest.OrderId)).Should().Be(0);
            await KillProbeAsync("cup-after-commit", crashRequest, crashRequest.OrderId);
            (await machine.PostAsJsonAsync("/internal/v2/kitchen/order-admissions", crashRequest, AdmissionProtocol.Json)).StatusCode.Should().Be(HttpStatusCode.OK);
            Guid cupId, ticketId;
            using (var scope = bar.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<KitchenOperationsDbContext>();
                (await db.Tickets.CountAsync(t => t.OrderId == orderId)).Should().Be(1);
                (await db.CupTasks.CountAsync(c => c.OrderId == orderId)).Should().Be(2);
                (await db.CupStickers.CountAsync(s => s.OrderId == orderId)).Should().Be(2);
                var cups = await db.CupTasks.Where(c => c.OrderId == orderId).OrderBy(c => c.OrderCupIndex).ToListAsync(); cupId = cups[0].Id; ticketId = cups[0].TicketId;
                cupId.Should().Be(CupIdentity.Create(tenant, orderId, admission.Manifest.Items.Single().OrderItemId, 1));
            }
            using var barista = bar.As(AicosRoles.Barista, branch);
            var staff1Id = Guid.NewGuid(); var staff2Id = Guid.NewGuid();
            using var staff1 = bar.As(AicosRoles.Cashier, branch, staff1Id);
            using var staff2 = bar.As(AicosRoles.Cashier, branch, staff2Id);
            (await barista.Put($"/api/v1/kitchen/tickets/{ticketId}/status", new { status = "Brewing" })).StatusCode.Should().Be(HttpStatusCode.Conflict);
            await ActAsync(barista, cupId, "start", "start", 0).ShouldBe(200);
            await ActAsync(barista, cupId, "ready", "ready", 1).ShouldBe(200);
            await ActAsync(barista, cupId, "claim", "barista-claim", 2).ShouldBe(403);
            var claims = await Task.WhenAll(ActAsync(staff1, cupId, "claim", "claim-a", 2), ActAsync(staff2, cupId, "claim", "claim-b", 2));
            claims.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1);
            claims.Count(r => r.StatusCode == HttpStatusCode.Conflict).Should().Be(1);
            var winner = claims[0].IsSuccessStatusCode ? staff1 : staff2;
            var key = claims[0].IsSuccessStatusCode ? "claim-a" : "claim-b";
            var state = await claims.Single(r => r.IsSuccessStatusCode).Body();
            var claimId = state.GetProperty("claim").GetProperty("id").GetGuid();
            var fence = state.GetProperty("claim").GetProperty("fence").GetInt64();
            await ActAsync(winner, cupId, "claim", key, 2).ShouldBe(200); // lost response replay
            await ActAsync(winner, cupId, "pickup", "bad-fence", 3, claimId, fence + 1).ShouldBe(409);
            await ActAsync(winner, cupId, "renew", "renew", 3, claimId, fence).ShouldBe(200);
            await KillProbeAsync("cup-operation-after-commit", new
            {
                Tenant = tenant,
                Branch = branch,
                Actor = claims[0].IsSuccessStatusCode ? staff1Id : staff2Id,
                Command = new CupCommand(cupId, "pickup", CupAction.Pickup, 4, 1, claimId, fence)
            }, cupId);
            await ActAsync(winner, cupId, "pickup", "pickup", 4, claimId, fence).ShouldBe(200);
            await ActAsync(winner, cupId, "release", "unsafe-release", 5, claimId, fence, true).ShouldBe(409);
            await ActAsync(winner, cupId, "serve", "serve", 5, claimId, fence).ShouldBe(200);
            await ActAsync(winner, cupId, "serve", "serve", 5, claimId, fence).ShouldBe(200);
            var snapshot = await LastSnapshotAsync(bar);
            snapshot.Served.Should().Be(1); snapshot.Total.Should().Be(2); snapshot.FulfilledAtUtc.Should().BeNull();
            using var receiver = orders.CreateClient();
            receiver.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", FulfillmentMachineAuthentication.Token(settings["CupServing__MachineKey"], tenant, branch));
            var transport = new HttpFulfillmentTransport(receiver, orders.Services.GetRequiredService<IConfiguration>());
            var first = await transport.SendAsync(snapshot, default); first.Status.Should().Be(200);
            var retry = await transport.SendAsync(snapshot, default); retry.Ack.Should().Be(first.Ack);
            await OperationalTestTarget.RestartRedisAsync(); // only the guarded newly-created test Redis
            // A committed receiver response is lost, then a new dispatcher/context retries the same durable rows.
            using (var scope = bar.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<KitchenOperationsDbContext>();
                await new FulfillmentDispatcher(db, new LostAckTransport(transport)).DispatchOnceAsync();
                (await db.FulfillmentOutbox.CountAsync(r => r.State == "PENDING")).Should().BeGreaterThan(0);
                await db.FulfillmentOutbox.Where(r => r.State == "PENDING").ExecuteUpdateAsync(s => s.SetProperty(r => r.NextAttemptAtUtc, DateTime.UtcNow.AddMinutes(-1)));
            }
            using (var scope = bar.Services.CreateScope())
                (await new FulfillmentDispatcher(scope.ServiceProvider.GetRequiredService<KitchenOperationsDbContext>(), transport).DispatchOnceAsync()).Should().BeGreaterThan(0);
            // An ACKED row is rechecked after a receiver projection loss, without an age cutoff.
            using (var scope = orders.Services.CreateScope())
                await scope.ServiceProvider.GetRequiredService<OrderingDbContext>().FulfillmentProjection.ExecuteDeleteAsync();
            using (var scope = bar.Services.CreateScope())
                await scope.ServiceProvider.GetRequiredService<KitchenOperationsDbContext>().FulfillmentOutbox.Where(r => r.State == "ACKED")
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.NextAttemptAtUtc, DateTime.UtcNow.AddMinutes(-1)));
            var worker1 = bar.Services.CreateScope(); var worker2 = bar.Services.CreateScope();
            try
            {
                await Task.WhenAll(new FulfillmentDispatcher(worker1.ServiceProvider.GetRequiredService<KitchenOperationsDbContext>(), transport).DispatchOnceAsync(),
                    new FulfillmentDispatcher(worker2.ServiceProvider.GetRequiredService<KitchenOperationsDbContext>(), transport).DispatchOnceAsync());
            }
            finally { worker1.Dispose(); worker2.Dispose(); }
            using (var scope = orders.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
                (await db.FulfillmentReceipts.Select(r => r.EventId).Distinct().CountAsync()).Should().Be(await db.FulfillmentReceipts.CountAsync());
                (await db.FulfillmentProjection.SingleAsync()).Served.Should().Be(1);
                (await db.Orders.SingleAsync()).IsPaid.Should().BeFalse();
            }
            // Replaying admission must preserve prepared/served cups and sticker identity.
            (await machine.PostAsJsonAsync("/internal/v2/kitchen/order-admissions", admission, AdmissionProtocol.Json)).StatusCode.Should().Be(HttpStatusCode.OK);
            Guid secondCup;
            using (var scope = bar.Services.CreateScope()) secondCup = await scope.ServiceProvider.GetRequiredService<KitchenOperationsDbContext>()
                .CupTasks.Where(c => c.OrderId == orderId && c.Id != cupId).Select(c => c.Id).SingleAsync();
            await ActAsync(barista, secondCup, "start", "second-start", 0).ShouldBe(200);
            await ActAsync(barista, secondCup, "ready", "second-ready", 1).ShouldBe(200);
            var reserveResponse = await ActAsync(staff1, secondCup, "claim", "second-claim", 2);
            reserveResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var reserved = await reserveResponse.Body();
            var secondClaim = reserved.GetProperty("claim").GetProperty("id").GetGuid();
            await ActAsync(staff1, secondCup, "release", "not-at-bar", 3, secondClaim, 1).ShouldBe(400);
            await ActAsync(staff1, secondCup, "release", "at-bar", 3, secondClaim, 1, true).ShouldBe(200);
            await ActAsync(staff2, secondCup, "claim", "reclaimed", 4).ShouldBe(200);
            using (var scope = bar.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<KitchenOperationsDbContext>();
                await db.CupClaims.Where(c => c.CupId == secondCup && c.State == CupClaimState.Reserved).ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.CreatedAtUtc, DateTime.UtcNow.AddMinutes(-2)).SetProperty(c => c.LeaseExpiresAtUtc, DateTime.UtcNow.AddMinutes(-1)));
                var live = await db.CupClaims.SingleAsync(c => c.CupId == secondCup && c.State == CupClaimState.Reserved);
                await ActAsync(staff2, secondCup, "pickup", "expired-pickup", 5, live.Id, live.Fence).ShouldBe(409);
                await ActAsync(staff1, secondCup, "claim", "no-stealing", 5).ShouldBe(409);
                (await db.CupClaims.CountAsync(c => c.CupId == secondCup && c.State == CupClaimState.Reserved)).Should().Be(1);
            }
            using (var scope = bar.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<KitchenOperationsDbContext>();
                (await db.CupTasks.SingleAsync(c => c.Id == cupId)).Service.Should().Be(CupService.Served);
                (await db.CupClaims.CountAsync(c => c.CupId == cupId)).Should().Be(1); (await db.CupStickers.CountAsync(s => s.OrderId == orderId)).Should().Be(2);
            }
            using (var scope = bar.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<KitchenOperationsDbContext>();
                var wrongTicket = await db.Tickets.Where(t => t.OrderId == crashRequest.OrderId).Select(t => t.Id).SingleAsync();
                await db.AdmissionReceipts.Where(r => r.EventId == admission.EventId).ExecuteUpdateAsync(s => s.SetProperty(r => r.TicketId, wrongTicket));
                (await machine.PostAsJsonAsync("/internal/v2/kitchen/order-admissions", admission, AdmissionProtocol.Json)).StatusCode.Should().Be(HttpStatusCode.Conflict);
                await db.AdmissionReceipts.Where(r => r.EventId == admission.EventId).ExecuteUpdateAsync(s => s.SetProperty(r => r.TicketId, ticketId));
            }
            var terminal = admission with { Source = admission.Source with { Status = "Cancelled", CancelledAtUtc = DateTime.UtcNow } };
            (await machine.PostAsJsonAsync("/internal/v2/kitchen/order-admissions", terminal, AdmissionProtocol.Json)).StatusCode.Should().Be(HttpStatusCode.Accepted);
            await ActAsync(staff2, secondCup, "pickup", "held-pickup", 5).ShouldBe(409);
            (await machine.PostAsJsonAsync("/internal/v2/kitchen/order-admissions", admission, AdmissionProtocol.Json)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        }
        finally { foreach (var pair in prior) Environment.SetEnvironmentVariable(pair.Key, pair.Value); }
    }

    private sealed class LostAckTransport(IFulfillmentTransport inner) : IFulfillmentTransport
    {
        public async Task<FulfillmentTransportResult> SendAsync(FulfillmentSnapshot snapshot, CancellationToken ct)
        { await inner.SendAsync(snapshot, ct); throw new HttpRequestException("Test lost ACK after receiver commit."); }
    }

    private sealed class AdmissionBridge(IKitchenAdmissionTransport inner) : IKitchenAdmissionTransport
    {
        public bool Offline = true;
        public Task<AdmissionTransportResult> SendCupAsync(CupAdmissionRequest request, CancellationToken ct) =>
            Offline ? throw new HttpRequestException("Test Kitchen offline.") : inner.SendCupAsync(request, ct);
        public Task<AdmissionTransportResult> SendAsync(AdmissionRequest request, CancellationToken ct) => inner.SendAsync(request, ct);
        public Task<IReadOnlyList<AdmissionPresence>> LookupAsync(Guid tenant, Guid branch, IReadOnlyList<Guid> orders, CancellationToken ct) => inner.LookupAsync(tenant, branch, orders, ct);
    }

    private static async Task KillProbeAsync(string mode, object input, Guid expected)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(OperationalTestTarget.Required("AICOS_OPERATIONS_CRASH_PROBE")); start.ArgumentList.Add(mode);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(input, AdmissionProtocol.Json)); process.StandardInput.Close();
            var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
            line.Should().Be("READY:" + expected);
            process.Kill(entireProcessTree: true); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } await error; }
    }

    private static Task<HttpResponseMessage> ActAsync(HttpClient client, Guid cup, string action, string key,
        long version, Guid? claim = null, long? fence = null, bool atBar = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/kitchen/cups/{cup}/{action}")
        { Content = JsonContent.Create(new { expectedVersion = version, expectedCycle = 1, claimId = claim, fence, confirmedAtBar = atBar }) };
        request.Headers.Add("Idempotency-Key", key); return client.SendAsync(request);
    }

    private static async Task<FulfillmentSnapshot> LastSnapshotAsync(ServiceFactory<KitchenOperationsService, KitchenOperationsDbContext> bar)
    {
        using var scope = bar.Services.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<KitchenOperationsDbContext>().FulfillmentOutbox.OrderByDescending(r => r.SourceVersion).FirstAsync();
        return JsonSerializer.Deserialize<FulfillmentSnapshot>(row.Payload)!;
    }

    internal static async Task ExpandAsync(DbContext db, string connection, string schema, string baseline, string script)
    {
        await db.GetService<IMigrator>().MigrateAsync(baseline);
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!Directory.Exists(Path.Combine(root.FullName, "docs/migrations/cup-serving"))) root = root.Parent ?? throw new InvalidOperationException("SQL bundle missing.");
        var bundle = Path.Combine(root.FullName, "docs/migrations/cup-serving");
        foreach (var line in await File.ReadAllLinesAsync(Path.Combine(bundle, "SHA256SUMS.txt")))
        {
            var parts = line.Split("  ", 2);
            Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.GetFullPath(Path.Combine(bundle, parts[1])))))
                .Should().BeEquivalentTo(parts[0]);
        }
        var target = new NpgsqlConnectionStringBuilder(connection);
        await using var sql = new NpgsqlConnection(connection); await sql.OpenAsync();
        await using var tableCount = new NpgsqlCommand("SELECT count(*) FROM information_schema.tables WHERE table_schema = @schema AND table_type = 'BASE TABLE'", sql);
        tableCount.Parameters.AddWithValue("schema", schema);
        var before = (long)(await tableCount.ExecuteScalarAsync())!;
        var start = new ProcessStartInfo("docker") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "exec", "-i", OperationalTestTarget.Required("AICOS_OPERATIONS_POSTGRES_ID"), "psql", "-X", "-U", target.Username!, "-d", target.Database!,
            "-v", "expected_database=" + target.Database, "-v", "expected_schema=" + schema, "-v", "expected_system_identifier=" + OperationalTestTarget.Required("AICOS_OPERATIONS_TEST_CLUSTER") }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(await File.ReadAllTextAsync(Path.Combine(bundle, "00_preflight.sql"))); process.StandardInput.Close();
        await process.WaitForExitAsync(); process.ExitCode.Should().Be(0, await output + await error);
        await using var command = new NpgsqlCommand(await File.ReadAllTextAsync(Path.Combine(bundle, script)), sql);
        await command.ExecuteNonQueryAsync();
        ((long)(await tableCount.ExecuteScalarAsync())!).Should().Be(before + (schema == "kitchen" ? 4 : 2));
        await using var foreignKeys = new NpgsqlCommand("SELECT count(*) FROM pg_constraint c JOIN pg_class t ON t.oid = c.conrelid JOIN pg_class f ON f.oid = c.confrelid JOIN pg_namespace n ON n.oid = t.relnamespace WHERE c.contype = 'f' AND n.nspname = @schema AND t.relnamespace <> f.relnamespace", sql);
        foreignKeys.Parameters.AddWithValue("schema", schema);
        ((long)(await foreignKeys.ExecuteScalarAsync())!).Should().Be(0);
        if (schema == "kitchen")
        {
            await using var index = new NpgsqlCommand("SELECT pg_get_indexdef(indexrelid) FROM pg_index WHERE indrelid = 'kitchen.cup_claims'::regclass AND indisunique AND indpred IS NOT NULL", sql);
            var definition = (string)(await index.ExecuteScalarAsync())!;
            definition.Should().Contain("Reserved").And.Contain("Carrying").And.Contain("Overdue");
        }
        (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
    }
}
