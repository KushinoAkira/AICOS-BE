using System.Net;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.TableReservation.Api;
using AICOS.TableReservation.Api.Domain;
using AICOS.TableReservation.Api.Features.TableOperations;
using AICOS.TableReservation.Api.Integrations;
using AICOS.TableReservation.Api.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AICOS.Services.SmokeTests;

public sealed class TableOperationsServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 23, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Table_relationships_do_not_cascade_delete()
    {
        using var context = CreateContext();

        context.Model.GetEntityTypes()
            .SelectMany(entity => entity.GetForeignKeys())
            .Should()
            .NotBeEmpty()
            .And.OnlyContain(foreignKey => foreignKey.DeleteBehavior == DeleteBehavior.NoAction);
    }

    [Fact]
    public async Task Table_map_endpoint_requires_an_access_token()
    {
        await using var factory = new WebApplicationFactory<TableReservationService>();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/tables/map");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Map_returns_only_tables_in_the_token_branch()
    {
        await using var context = CreateContext();
        var own = await SeedTableAsync(context, "S02", "Tầng trệt");
        await SeedTableAsync(context, "L01", "Tầng lầu", Guid.NewGuid());
        var service = CreateService(context);

        var map = await service.GetMapAsync(
            new TestCurrentUser(own.BranchId),
            CancellationToken.None);

        map.BranchId.Should().Be(own.BranchId);
        map.Areas.Should().ContainSingle();
        map.Areas[0].Tables.Should().ContainSingle(table => table.Id == own.TableId);
    }

    [Fact]
    public async Task Open_close_and_clean_table_follow_the_expected_state_machine()
    {
        await using var context = CreateContext();
        var data = await SeedTableAsync(context, "S02", "Tầng trệt");
        var currentUser = new TestCurrentUser(data.BranchId);
        var service = CreateService(context);

        var opened = await service.OpenSessionAsync(
            new OpenTableSessionRequest(data.TableId),
            currentUser,
            CancellationToken.None);

        opened.Status.Should().Be(TableSessionStatuses.Open);
        opened.TableIds.Should().Equal(data.TableId);
        var loaded = await service.GetSessionAsync(
            opened.Id,
            currentUser,
            CancellationToken.None);
        loaded.TableIds.Should().Equal(data.TableId);
        (await context.CafeTables.FindAsync(data.TableId))!.Status
            .Should().Be(CafeTableStatuses.Occupied);

        var openAgain = () => service.OpenSessionAsync(
            new OpenTableSessionRequest(data.TableId),
            currentUser,
            CancellationToken.None);
        var openConflict = await openAgain.Should().ThrowAsync<ConflictException>();
        openConflict.Which.ErrorCode.Should().Be("table.not_available");

        var closed = await service.CloseSessionAsync(
            opened.Id,
            currentUser,
            CancellationToken.None);
        closed.Status.Should().Be(TableSessionStatuses.Closed);
        closed.ClosedAt.Should().Be(Now.DateTime);
        (await context.CafeTables.FindAsync(data.TableId))!.Status
            .Should().Be(CafeTableStatuses.Cleaning);

        var available = await service.MarkCleanedAsync(
            data.TableId,
            currentUser,
            CancellationToken.None);
        available.Status.Should().Be(CafeTableStatuses.Available);
    }

    [Fact]
    public async Task Staff_cannot_operate_on_a_table_from_another_branch()
    {
        await using var context = CreateContext();
        var table = await SeedTableAsync(context, "S02", "Tầng trệt");
        var service = CreateService(context);

        var action = () => service.OpenSessionAsync(
            new OpenTableSessionRequest(table.TableId),
            new TestCurrentUser(Guid.NewGuid()),
            CancellationToken.None);

        await action.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Close_session_is_blocked_when_payment_is_not_settled()
    {
        await using var context = CreateContext();
        var data = await SeedTableAsync(context, "S02", "Ground floor");
        var currentUser = new TestCurrentUser(data.BranchId);
        var opened = await CreateService(context).OpenSessionAsync(
            new OpenTableSessionRequest(data.TableId),
            currentUser,
            CancellationToken.None);
        var service = CreateService(context, new FixedSettlementClient(false));

        var action = () => service.CloseSessionAsync(
            opened.Id,
            currentUser,
            CancellationToken.None);

        var conflict = await action.Should().ThrowAsync<ConflictException>();
        conflict.Which.ErrorCode.Should().Be("table.session_not_settled");
        (await context.TableSessions.FindAsync(opened.Id))!.Status
            .Should().Be(TableSessionStatuses.Open);
        (await context.CafeTables.FindAsync(data.TableId))!.Status
            .Should().Be(CafeTableStatuses.Occupied);
    }

    private static TableReservationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TableReservationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new TableReservationDbContext(options);
    }

    private static TableOperationsService CreateService(
        TableReservationDbContext context,
        IPaymentSettlementClient? payments = null) =>
        new(
            context,
            payments ?? new FixedSettlementClient(true),
            new FixedTimeProvider(Now),
            NullLogger<TableOperationsService>.Instance);

    private static async Task<TestTableData> SeedTableAsync(
        TableReservationDbContext context,
        string tableNumber,
        string areaName,
        Guid? branchId = null)
    {
        var resolvedBranchId = branchId ?? Guid.NewGuid();
        var area = new TableArea(Guid.NewGuid(), resolvedBranchId, areaName);
        var table = new CafeTable(
            Guid.NewGuid(),
            area.Id,
            tableNumber,
            4,
            $"qr-{Guid.NewGuid():N}");
        context.AddRange(area, table);
        await context.SaveChangesAsync();
        return new TestTableData(resolvedBranchId, table.Id);
    }

    private sealed record TestTableData(Guid BranchId, Guid TableId);

    private sealed class TestCurrentUser(Guid branchId) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid? UserId { get; } = Guid.NewGuid();
        public string? Name => "Table Test Staff";
        public string? Role => AicosRoles.Waiter;
        public Guid? BranchId => branchId;
        public bool IsInRole(string role) => role == AicosRoles.Waiter;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class FixedSettlementClient(bool settled) : IPaymentSettlementClient
    {
        public Task<PaymentSettlementContract> GetAsync(
            Guid sessionId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new PaymentSettlementContract(
                sessionId,
                settled ? Guid.NewGuid() : null,
                settled ? "CLOSED" : null,
                settled ? 100m : 0m,
                settled ? 100m : 0m,
                0m,
                false,
                settled,
                settled ? 3 : 0));
    }
}
