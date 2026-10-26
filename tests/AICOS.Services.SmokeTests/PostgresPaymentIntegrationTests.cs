using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.IdentityOrganization.Api.Domain;
using AICOS.IdentityOrganization.Api.Persistence;
using AICOS.Payments.Api;
using AICOS.Payments.Api.Domain;
using AICOS.Payments.Api.Features.Billing;
using AICOS.Payments.Api.Features.Payments;
using AICOS.Payments.Api.Persistence;
using AICOS.TableReservation.Api.Domain;
using AICOS.TableReservation.Api.Features.TableOperations;
using AICOS.TableReservation.Api.Integrations;
using AICOS.TableReservation.Api.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AICOS.Services.SmokeTests;

public sealed class PostgresPaymentIntegrationTests
{
    [PostgresIntegrationFact]
    public async Task Cash_qr_audit_concurrency_and_table_settlement_use_dedicated_postgres()
    {
        var connectionString = Environment.GetEnvironmentVariable("AICOS_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            if (string.Equals(
                    Environment.GetEnvironmentVariable("AICOS_REQUIRE_POSTGRES_INTEGRATION"),
                    "true",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "AICOS_TEST_POSTGRES is required for the PostgreSQL integration test.");
            }

            return;
        }

        PaymentPostgresTestDatabaseGuard.RequireDedicatedDatabase(connectionString);
        var ids = PaymentTestIds.Create();
        await SeedAsync(connectionString, ids);

        const string signingKey = "payment-integration-key-0123456789-abcdef";
        await using var factory = CreatePaymentFactory(connectionString, signingKey);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            CreateToken(ids, signingKey));

        await VerifyCashAsync(client, connectionString, ids);
        await VerifyQrAndTableSettlementAsync(client, connectionString, ids);
    }

    private static async Task VerifyCashAsync(
        HttpClient client,
        string connectionString,
        PaymentTestIds ids)
    {
        var request = new CreatePaymentRequest(ids.CashPaymentId, 63000m, 0);
        var created = await client.PostAsJsonAsync(
            $"/api/v1/payments/bills/{ids.CashBillId}/payments/cash",
            request);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var payment = (await created.Content.ReadFromJsonAsync<PaymentResponse>())!;
        payment.Status.Should().Be(PaymentStatuses.Succeeded);
        payment.ConfirmedByUserId.Should().Be(ids.UserId);

        var replay = await client.PostAsJsonAsync(
            $"/api/v1/payments/bills/{ids.CashBillId}/payments/cash",
            request);
        replay.EnsureSuccessStatusCode();

        var close = await client.PostAsJsonAsync(
            $"/api/v1/payments/bills/{ids.CashBillId}/close",
            new CloseBillRequest(payment.BillVersion));
        close.EnsureSuccessStatusCode();

        await using var db = CreatePaymentContext(connectionString);
        (await db.Payments.CountAsync(item => item.Id == ids.CashPaymentId)).Should().Be(1);
        (await db.PaymentEvents.CountAsync(item => item.PaymentId == ids.CashPaymentId))
            .Should().Be(1);
        (await db.Bills.SingleAsync(item => item.Id == ids.CashBillId)).Status
            .Should().Be(BillStatuses.Closed);
    }

    private static async Task VerifyQrAndTableSettlementAsync(
        HttpClient client,
        string connectionString,
        PaymentTestIds ids)
    {
        var create = await client.PostAsJsonAsync(
            $"/api/v1/payments/bills/{ids.QrBillId}/payments/qr",
            new CreatePaymentRequest(ids.QrPaymentId, 110000m, 0));
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var pending = (await create.Content.ReadFromJsonAsync<PaymentResponse>())!;
        pending.Status.Should().Be(PaymentStatuses.Pending);

        var currentUser = new IntegrationCurrentUser(ids.BranchId, ids.UserId);
        await using (var tableDb = CreateTableContext(connectionString))
        {
            var tableService = CreateTableService(tableDb, client);
            var closeUnpaid = () => tableService.CloseSessionAsync(
                ids.QrSessionId,
                currentUser,
                CancellationToken.None);
            var conflict = await closeUnpaid.Should().ThrowAsync<AICOS.BuildingBlocks.Exceptions.ConflictException>();
            conflict.Which.ErrorCode.Should().Be("table.session_not_settled");
            (await tableDb.TableSessions.FindAsync(ids.QrSessionId))!.Status
                .Should().Be(TableSessionStatuses.Open);
        }

        var invalid = await client.PostAsJsonAsync(
            $"/api/v1/payments/payments/{ids.QrPaymentId}/confirm",
            new ConfirmBankTransferRequest(Guid.NewGuid(), 0, " ", null));
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var firstRequestId = Guid.NewGuid();
        var secondRequestId = Guid.NewGuid();
        var firstTask = client.PostAsJsonAsync(
            $"/api/v1/payments/payments/{ids.QrPaymentId}/confirm",
            new ConfirmBankTransferRequest(firstRequestId, 0, "BANK-TRACE-001", "Cashier verified"));
        var secondTask = client.PostAsJsonAsync(
            $"/api/v1/payments/payments/{ids.QrPaymentId}/confirm",
            new ConfirmBankTransferRequest(secondRequestId, 0, "BANK-TRACE-002", "Cashier verified"));
        var confirmations = await Task.WhenAll(firstTask, secondTask);
        confirmations.Count(response => response.StatusCode == HttpStatusCode.OK).Should().Be(1);
        confirmations.Count(response => response.StatusCode == HttpStatusCode.Conflict).Should().Be(1);

        var winnerIndex = Array.FindIndex(confirmations, response => response.StatusCode == HttpStatusCode.OK);
        var winningRequestId = winnerIndex == 0 ? firstRequestId : secondRequestId;
        var winningEvidence = winnerIndex == 0 ? "BANK-TRACE-001" : "BANK-TRACE-002";
        var confirmed = (await confirmations[winnerIndex].Content.ReadFromJsonAsync<PaymentResponse>())!;
        confirmed.Status.Should().Be(PaymentStatuses.Succeeded);
        confirmed.EvidenceReference.Should().Be(winningEvidence);

        var confirmationReplay = await client.PostAsJsonAsync(
            $"/api/v1/payments/payments/{ids.QrPaymentId}/confirm",
            new ConfirmBankTransferRequest(
                winningRequestId,
                0,
                winningEvidence,
                "Cashier verified"));
        confirmationReplay.StatusCode.Should().Be(HttpStatusCode.OK);

        var beforeClose = await client.GetFromJsonAsync<SettlementStatusResponse>(
            $"/api/v1/payments/sessions/{ids.QrSessionId}/settlement-status");
        beforeClose!.Settled.Should().BeFalse();
        beforeClose.BillStatus.Should().Be(BillStatuses.Paid);

        var closeBill = await client.PostAsJsonAsync(
            $"/api/v1/payments/bills/{ids.QrBillId}/close",
            new CloseBillRequest(beforeClose.Version));
        closeBill.EnsureSuccessStatusCode();
        var settled = await client.GetFromJsonAsync<SettlementStatusResponse>(
            $"/api/v1/payments/sessions/{ids.QrSessionId}/settlement-status");
        settled!.Settled.Should().BeTrue();

        await using (var tableDb = CreateTableContext(connectionString))
        {
            var closed = await CreateTableService(tableDb, client).CloseSessionAsync(
                ids.QrSessionId,
                currentUser,
                CancellationToken.None);
            closed.Status.Should().Be(TableSessionStatuses.Closed);
        }

        await using var db = CreatePaymentContext(connectionString);
        (await db.Payments.CountAsync(item => item.Id == ids.QrPaymentId)).Should().Be(1);
        var events = await db.PaymentEvents
            .Where(item => item.PaymentId == ids.QrPaymentId)
            .OrderBy(item => item.OccurredAt)
            .ToListAsync();
        events.Should().HaveCount(2);
        events.Select(item => item.EventType)
            .Should().BeEquivalentTo([PaymentEventTypes.Created, PaymentEventTypes.Confirmed]);
        events.Single(item => item.EventType == PaymentEventTypes.Confirmed)
            .EvidenceReference.Should().Be(winningEvidence);
    }

    private static WebApplicationFactory<PaymentsService> CreatePaymentFactory(
        string connectionString,
        string signingKey) =>
        new WebApplicationFactory<PaymentsService>()
            .WithWebHostBuilder(builder => builder
                .UseSetting("ConnectionStrings:Default", connectionString)
                .UseSetting("Database:MigrateOnStartup", "false")
                .UseSetting("Jwt:Issuer", "aicos-payment-integration")
                .UseSetting("Jwt:Audience", "aicos-payment-clients")
                .UseSetting("Jwt:SigningKey", signingKey));

    private static string CreateToken(PaymentTestIds ids, string signingKey)
    {
        var tokens = new JwtTokenService(
            Options.Create(new JwtOptions
            {
                Issuer = "aicos-payment-integration",
                Audience = "aicos-payment-clients",
                SigningKey = signingKey,
                ExpiryMinutes = 30,
            }),
            TimeProvider.System);
        return tokens.CreateAccessToken(
            ids.UserId,
            "Payment Integration Cashier",
            AicosRoles.Cashier,
            ids.BranchId).Token;
    }

    private static async Task SeedAsync(string connectionString, PaymentTestIds ids)
    {
        await using (var identity = CreateIdentityContext(connectionString))
        {
            identity.AddRange(
                new Tenant(ids.TenantId, "Payment Test Tenant", ids.Code),
                new Branch(ids.BranchId, ids.TenantId, ids.Code, "Payment Test Branch", IdentityStatuses.Active),
                new UserAccount(ids.UserId, $"payment-{ids.Code}@aicos.test", "Payment Test Cashier"));
            await identity.SaveChangesAsync();
        }

        await using (var tables = CreateTableContext(connectionString))
        {
            var area = new TableArea(ids.AreaId, ids.BranchId, "Payment Test Area");
            var table = new CafeTable(ids.TableId, ids.AreaId, ids.Code, 4, $"qr-{ids.Code}");
            var qrSession = new TableSession(ids.QrSessionId, ids.BranchId, DateTime.Now);
            var cashSession = new TableSession(ids.CashSessionId, ids.BranchId, DateTime.Now);
            var link = new TableSessionTable(Guid.NewGuid(), ids.QrSessionId, ids.TableId, DateTime.Now);
            table.MarkOccupied();
            tables.AddRange(area, table, qrSession, cashSession, link);
            await tables.SaveChangesAsync();
        }

        await using var payments = CreatePaymentContext(connectionString);
        payments.Bills.AddRange(
            new Bill(ids.CashBillId, ids.BranchId, ids.UserId, ids.CashSessionId, null, $"BL-C-{ids.Code}", 63000m, DateTime.Now),
            new Bill(ids.QrBillId, ids.BranchId, ids.UserId, ids.QrSessionId, null, $"BL-Q-{ids.Code}", 110000m, DateTime.Now));
        await payments.SaveChangesAsync();
    }

    private static TableOperationsService CreateTableService(
        TableReservationDbContext dbContext,
        HttpClient paymentClient) =>
        new(
            dbContext,
            new PaymentSettlementClient(paymentClient),
            TimeProvider.System,
            NullLogger<TableOperationsService>.Instance);

    private static PaymentsDbContext CreatePaymentContext(string connectionString) =>
        new(new DbContextOptionsBuilder<PaymentsDbContext>()
            .UseNpgsql(connectionString).UseSnakeCaseNamingConvention().Options);

    private static TableReservationDbContext CreateTableContext(string connectionString) =>
        new(new DbContextOptionsBuilder<TableReservationDbContext>()
            .UseNpgsql(connectionString).UseSnakeCaseNamingConvention().Options);

    private static IdentityOrganizationDbContext CreateIdentityContext(string connectionString) =>
        new(new DbContextOptionsBuilder<IdentityOrganizationDbContext>()
            .UseNpgsql(connectionString).UseSnakeCaseNamingConvention().Options);

    private sealed class IntegrationCurrentUser(Guid branchId, Guid userId) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid? UserId { get; } = userId;
        public string? Name => "Payment Integration Cashier";
        public string? Role => AicosRoles.Cashier;
        public Guid? BranchId { get; } = branchId;
        public bool IsInRole(string role) => role == AicosRoles.Cashier;
    }

    private sealed record PaymentTestIds(
        Guid TenantId,
        Guid BranchId,
        Guid UserId,
        Guid AreaId,
        Guid TableId,
        Guid QrSessionId,
        Guid CashSessionId,
        Guid QrBillId,
        Guid CashBillId,
        Guid QrPaymentId,
        Guid CashPaymentId,
        string Code)
    {
        public static PaymentTestIds Create()
        {
            var code = Guid.NewGuid().ToString("N")[..10];
            return new PaymentTestIds(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), code);
        }
    }
}
