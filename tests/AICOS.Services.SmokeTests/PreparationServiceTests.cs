using System.Net;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.KitchenOperations.Api;
using AICOS.KitchenOperations.Api.Domain;
using AICOS.KitchenOperations.Api.Features.Preparation;
using AICOS.KitchenOperations.Api.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AICOS.Services.SmokeTests;

public sealed class PreparationServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 23, 14, 15, 0, TimeSpan.Zero);

    [Fact]
    public void Preparation_relationships_do_not_cascade_delete()
    {
        using var context = CreateContext();

        context.Model.GetEntityTypes()
            .SelectMany(entity => entity.GetForeignKeys())
            .Should()
            .NotBeEmpty()
            .And.OnlyContain(foreignKey => foreignKey.DeleteBehavior == DeleteBehavior.NoAction);
    }

    [Fact]
    public async Task Preparation_queue_endpoint_requires_an_access_token()
    {
        await using var factory = new WebApplicationFactory<KitchenOperationsService>();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/kitchen/queue");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Queue_is_branch_scoped_and_hides_ready_items()
    {
        await using var context = CreateContext();
        var ownBranchId = Guid.NewGuid();
        var ownPending = await SeedItemAsync(context, ownBranchId, "BK-881");
        var ownReady = await SeedItemAsync(context, ownBranchId, "BK-882");
        await SeedItemAsync(context, Guid.NewGuid(), "BK-883");
        ownReady.MoveTo(PreparationStatuses.Preparing);
        ownReady.MoveTo(PreparationStatuses.Ready);
        await context.SaveChangesAsync();
        var service = CreateService(context);

        var queue = await service.GetQueueAsync(
            new TestCurrentUser(ownBranchId),
            CancellationToken.None);

        queue.BranchId.Should().Be(ownBranchId);
        queue.Orders.Should().ContainSingle();
        queue.Orders[0].Items.Should().ContainSingle(item => item.Id == ownPending.Id);
    }

    [Fact]
    public async Task Item_follows_preparation_state_machine_and_records_actor()
    {
        await using var context = CreateContext();
        var branchId = Guid.NewGuid();
        var item = await SeedItemAsync(context, branchId, "BK-881");
        var currentUser = new TestCurrentUser(branchId);
        var service = CreateService(context);

        var preparing = await service.UpdateAsync(
            item.Id,
            new UpdatePreparationStatusRequest(PreparationStatuses.Preparing),
            currentUser,
            CancellationToken.None);
        var ready = await service.UpdateAsync(
            item.Id,
            new UpdatePreparationStatusRequest(PreparationStatuses.Ready),
            currentUser,
            CancellationToken.None);

        preparing.Status.Should().Be(PreparationStatuses.Preparing);
        ready.Status.Should().Be(PreparationStatuses.Ready);
        var events = await context.Events
            .Where(preparationEvent => preparationEvent.OrderItemId == item.Id)
            .OrderBy(preparationEvent => preparationEvent.CreatedAt)
            .ToListAsync();
        events.Should().HaveCount(2);
        events.Should().OnlyContain(preparationEvent =>
            preparationEvent.ActorUserId == currentUser.UserId);
        events.Select(preparationEvent => preparationEvent.NewStatus)
            .Should().Equal(PreparationStatuses.Preparing, PreparationStatuses.Ready);

        var invalidAction = () => service.UpdateAsync(
            item.Id,
            new UpdatePreparationStatusRequest(PreparationStatuses.Preparing),
            currentUser,
            CancellationToken.None);
        var conflict = await invalidAction.Should().ThrowAsync<ConflictException>();
        conflict.Which.ErrorCode.Should().Be("preparation.invalid_transition");
    }

    private static KitchenOperationsDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<KitchenOperationsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new KitchenOperationsDbContext(options);
    }

    private static PreparationService CreateService(KitchenOperationsDbContext context) =>
        new(
            context,
            new FixedTimeProvider(Now),
            NullLogger<PreparationService>.Instance);

    private static async Task<PreparationItem> SeedItemAsync(
        KitchenOperationsDbContext context,
        Guid branchId,
        string orderCode)
    {
        var order = new PreparationOrder(
            Guid.NewGuid(),
            branchId,
            orderCode,
            "DINE_IN",
            Now.DateTime);
        var item = new PreparationItem(
            Guid.NewGuid(),
            order.Id,
            "Cà phê sữa",
            "M",
            1,
            "Ít đá");
        context.AddRange(order, item);
        await context.SaveChangesAsync();
        return item;
    }

    private sealed class TestCurrentUser(Guid branchId) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid? UserId { get; } = Guid.NewGuid();
        public string? Name => "Preparation Test Manager";
        public string? Role => AicosRoles.Manager;
        public Guid? BranchId => branchId;
        public bool IsInRole(string role) => role == AicosRoles.Manager;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
