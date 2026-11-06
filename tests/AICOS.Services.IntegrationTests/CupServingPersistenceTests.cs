using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Serving;
using AICOS.KitchenOperations.Api;
using AICOS.KitchenOperations.Api.Domain.Entities;
using AICOS.KitchenOperations.Api.Features.CupServing;
using AICOS.KitchenOperations.Api.Features.KitchenAdmission;
using AICOS.KitchenOperations.Api.Persistence;
using AICOS.Ordering.Api;
using AICOS.Ordering.Api.Domain.Entities;
using AICOS.Ordering.Api.Domain.Enums;
using AICOS.Ordering.Api.Features.Fulfillment;
using AICOS.Ordering.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AICOS.Services.IntegrationTests;

/// <summary>Persistence/core integration using EF InMemory, not proof of PostgreSQL isolation or crash atomicity.</summary>
public sealed class CupServingPersistenceTests
{
    private sealed record Staff(Guid Tenant, Guid Branch, Guid Actor, string Role) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid? UserId => Actor;
        public string? Name => "Test staff";
        public Guid? TenantId => Tenant;
        public Guid? BranchId => Branch;
        public bool IsInRole(string role) => Role == role;
    }

    [Fact]
    public async Task Core_commits_audit_claim_and_outbox_and_replays_without_duplicate_effect()
    {
        using var f = new ServiceFactory<KitchenOperationsService, KitchenOperationsDbContext>();
        var tenant = ServiceFactory<KitchenOperationsService, KitchenOperationsDbContext>.DefaultTenantId;
        var branch = f.Branches.Add(tenant); var actor = Guid.NewGuid(); var order = Guid.NewGuid(); var sourceItem = Guid.NewGuid();
        using var scope = f.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<KitchenOperationsDbContext>();
        var now = f.Clock.Now.UtcDateTime;
        var ticket = new KitchenTicket(tenant, order, "CUP-CORE", branch, "S01", "DineIn", false);
        ticket.AddItem(Guid.NewGuid(), "Coffee", 1); db.Tickets.Add(ticket);
        db.Entry(ticket).Property(t => t.FulfillmentMode).CurrentValue = FulfillmentModes.CupV1;
        var item = ticket.Items.Single(); db.Entry(item).Property(i => i.OrderItemId).CurrentValue = sourceItem;
        var cup = new CupTask(CupIdentity.Create(tenant, order, sourceItem, 1), tenant, branch, order, ticket.Id, item.Id, sourceItem, 1, 1, now);
        db.CupTasks.Add(cup);
        db.AdmissionReceipts.Add(new AdmissionReceipt { ConsumerKey = "kitchen-order-admission-v2", EventId = Guid.NewGuid(),
            TenantId = tenant, BranchId = branch, OrderId = order, TicketId = ticket.Id, CreationSha256 = new byte[32],
            ReceiptStatus = "APPLIED", RecordedAtUtc = now, AppliedAtUtc = now, LastVerifiedAtUtc = now });
        await db.SaveChangesAsync();
        var bar = new CupServingUnitOfWork(db, new Staff(tenant, branch, actor, AicosRoles.Barista), f.Clock, TimeSpan.FromMinutes(1));
        await bar.ExecuteAsync(new(cup.Id, "start", CupAction.Start, 0, 1));
        await bar.ExecuteAsync(new(cup.Id, "ready", CupAction.Ready, 1, 1));
        await Assert.ThrowsAsync<AICOS.BuildingBlocks.Exceptions.ForbiddenException>(() =>
            bar.ExecuteAsync(new(cup.Id, "forbidden-claim", CupAction.Claim, 2, 1)));
        var staff = new CupServingUnitOfWork(db, new Staff(tenant, branch, actor, AicosRoles.Cashier), f.Clock, TimeSpan.FromMinutes(1));
        var claimCommand = new CupCommand(cup.Id, "claim", CupAction.Claim, 2, 1);
        var claimed = await staff.ExecuteAsync(claimCommand); var replay = await staff.ExecuteAsync(claimCommand);
        replay.Replayed.Should().BeTrue(); replay.OperationId.Should().Be(claimed.OperationId);
        replay.Claim!.Id.Should().Be(claimed.Claim!.Id);
        await Assert.ThrowsAsync<AICOS.BuildingBlocks.Exceptions.ConflictException>(() =>
            staff.ExecuteAsync(claimCommand with { ExpectedVersion = 999 }));
        var claim = replay.Claim;
        await staff.ExecuteAsync(new(cup.Id, "pickup", CupAction.Pickup, 3, 1, claim.Id, claim.Fence));
        var serve = new CupCommand(cup.Id, "serve", CupAction.Serve, 4, 1, claim.Id, claim.Fence);
        await staff.ExecuteAsync(serve); var again = await staff.ExecuteAsync(serve);
        again.Replayed.Should().BeTrue(); again.Cup.Service.Should().Be(CupService.Served);
        (await db.CupClaims.CountAsync()).Should().Be(1);
        (await db.CupOperations.CountAsync()).Should().Be(5);
        (await db.FulfillmentOutbox.CountAsync()).Should().Be(5);
        var savedTicket = await db.Tickets.SingleAsync(); savedTicket.Status.ToString().Should().Be("Completed");
        savedTicket.IsPaid.Should().BeFalse(); savedTicket.FulfillmentVersion.Should().Be(5);
        savedTicket.HoldRecovery("review-after-effect"); await db.SaveChangesAsync();
        var heldReplay = await staff.ExecuteAsync(serve);
        heldReplay.Replayed.Should().BeTrue(); heldReplay.TicketHoldReason.Should().Be("review-after-effect");
        heldReplay.ServerTimeUtc.Should().Be(f.Clock.Now.UtcDateTime);
        (await db.CupOperations.CountAsync()).Should().Be(5);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Operation_audit_cannot_be_updated_or_deleted_through_the_context(bool delete)
    {
        using var f = new ServiceFactory<KitchenOperationsService, KitchenOperationsDbContext>();
        using var scope = f.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<KitchenOperationsDbContext>();
        var operation = new CupOperation { TenantId = Guid.NewGuid(), ActorId = Guid.NewGuid(), CupId = Guid.NewGuid(),
            OperationKey = "audit", Command = "Claim", VersionBefore = 0, VersionAfter = 1,
            RequestHash = new string('A', 64), CommittedAtUtc = DateTime.UtcNow };
        db.CupOperations.Add(operation); await db.SaveChangesAsync();
        if (delete) db.CupOperations.Remove(operation);
        else db.Entry(operation).Property(o => o.Reason).CurrentValue = "Tampered";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Inbox_projection_retries_do_not_touch_order_financial_or_session_fields()
    {
        using var f = new ServiceFactory<OrderingService, OrderingDbContext>();
        var tenant = ServiceFactory<OrderingService, OrderingDbContext>.DefaultTenantId; var branch = f.Branches.Add(tenant);
        using var scope = f.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        var order = new Order(tenant, branch, OrderType.DineIn, f.Clock.Now.UtcDateTime);
        order.AddItem(Guid.NewGuid(), "Coffee", 50000, 1); db.Orders.Add(order);
        db.Entry(order).Property(o => o.FulfillmentMode).CurrentValue = FulfillmentModes.CupV1;
        await db.SaveChangesAsync(); var originalStatus = order.Status;
        var snapshot = new FulfillmentSnapshot(Guid.NewGuid(), tenant, branch, order.Id, Guid.NewGuid(), 1,
            new string('A', 64), 1, 0, 0, 0, 0, 1, 0, f.Clock.Now.UtcDateTime);
        db.AdmissionOutbox.Add(new AICOS.Ordering.Api.Features.KitchenAdmission.AdmissionOutbox
        {
            TenantId = tenant, BranchId = branch, OrderId = order.Id, ConsumerKey = "kitchen-order-admission-v2",
            SchemaVersion = 2, CreationPayload = "{}", CreationSha256 = Convert.FromHexString(snapshot.ManifestHash),
            CreatedAtUtc = f.Clock.Now.UtcDateTime, NextAttemptAtUtc = f.Clock.Now.UtcDateTime
        });
        await db.SaveChangesAsync();
        var writer = new FulfillmentProjectionWriter(db, f.Clock);
        var ack = await writer.ApplyAsync(snapshot); (await writer.ApplyAsync(snapshot)).Should().Be(ack);
        (await db.FulfillmentReceipts.CountAsync()).Should().Be(1);
        (await db.FulfillmentProjection.CountAsync()).Should().Be(1);
        var saved = await db.Orders.Include(o => o.Items).SingleAsync(); saved.PaymentStatus.Should().Be(PaymentStatus.Unpaid);
        saved.Status.Should().Be(originalStatus); saved.DeliveredAtUtc.Should().BeNull(); saved.CompletedAtUtc.Should().BeNull();
        saved.TableSessionId.Should().BeNull(); saved.FinalAmount.Should().Be(50000);
    }
}
