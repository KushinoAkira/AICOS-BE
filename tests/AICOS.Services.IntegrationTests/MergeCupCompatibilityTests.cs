using System.Text.Json;
using System.Text.Json.Nodes;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;
using AICOS.Contracts.KitchenAdmission;
using AICOS.Contracts.Serving;
using AICOS.KitchenOperations.Api;
using AICOS.KitchenOperations.Api.Features.CupServing;
using AICOS.KitchenOperations.Api.Persistence;
using AICOS.Ordering.Api;
using AICOS.Ordering.Api.Domain.Entities;
using AICOS.Ordering.Api.Domain.Enums;
using AICOS.Ordering.Api.Features.KitchenAdmission;
using AICOS.Ordering.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AICOS.Services.IntegrationTests;

/// <summary>Merge compatibility: EF InMemory/test identity, not PostgreSQL/Redis E2E.</summary>
public sealed class MergeCupCompatibilityTests
{
    [Fact]
    public void Merged_models_match_existing_migration_snapshots_without_connecting_to_PostgreSQL()
    {
        const string metadataOnly = "Host=127.0.0.1;Port=1;Database=metadata_only;Username=unused;Password=unused";
        using var kitchen = new KitchenOperationsDbContext(new DbContextOptionsBuilder<KitchenOperationsDbContext>()
            .UseNpgsql(metadataOnly).UseSnakeCaseNamingConvention().Options);
        using var orders = new OrderingDbContext(new DbContextOptionsBuilder<OrderingDbContext>()
            .UseNpgsql(metadataOnly).UseSnakeCaseNamingConvention().Options);
        kitchen.Database.HasPendingModelChanges().Should().BeFalse();
        orders.Database.HasPendingModelChanges().Should().BeFalse();
    }

    private sealed record Barista(Guid Tenant, Guid Branch) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid? UserId => Guid.Parse("22222222-2222-2222-2222-222222222222");
        public string? Name => "Merge test";
        public Guid? TenantId => Tenant;
        public Guid? BranchId => Branch;
        public string? Role => AicosRoles.Barista;
        public bool IsInRole(string role) => role == AicosRoles.Barista;
    }

    private static CupAdmissionRequest Request(Guid tenant, Guid branch, bool payFirst = true)
    {
        var order = Guid.NewGuid();
        var manifest = new CupManifest("MERGE-CUP", FulfillmentModes.CupV1, Guid.NewGuid(), Guid.NewGuid(),
            [new(Guid.NewGuid(), Guid.NewGuid(), "Coffee", 2, null)]);
        return new(Guid.NewGuid(), tenant, branch, order, manifest,
            CupAdmissionProtocol.Hash(tenant, branch, order, manifest),
            new("DineIn", "Pending", false, "S01", DateTime.UtcNow, null, null, null, payFirst));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Event_json_preserves_both_flags_and_older_payload_defaults(bool payFirst)
    {
        var message = new OrderCreatedIntegrationEvent(Guid.NewGuid(), Guid.NewGuid(), "MERGE", Guid.NewGuid(),
            null, null, null, null, "DineIn", 100, false, null, [], DateTime.UtcNow,
            PayFirst: payFirst, FulfillmentMode: FulfillmentModes.CupV1);
        var json = JsonSerializer.Serialize(message, AdmissionProtocol.Json);
        var decoded = JsonSerializer.Deserialize<OrderCreatedIntegrationEvent>(json, AdmissionProtocol.Json)!;
        decoded.PayFirst.Should().Be(payFirst);
        decoded.FulfillmentMode.Should().Be(FulfillmentModes.CupV1);
        var legacy = JsonNode.Parse(json)!.AsObject();
        legacy.Remove("fulfillmentMode");
        JsonSerializer.Deserialize<OrderCreatedIntegrationEvent>(legacy.ToJsonString(), AdmissionProtocol.Json)!
            .FulfillmentMode.Should().Be(FulfillmentModes.WholeOrder);
        legacy.Remove("payFirst");
        JsonSerializer.Deserialize<OrderCreatedIntegrationEvent>(legacy.ToJsonString(), AdmissionProtocol.Json)!
            .PayFirst.Should().BeFalse();
    }

    [Fact]
    public async Task Both_admission_versions_carry_guest_QR_payment_policy()
    {
        using var f = new ServiceFactory<OrderingService, OrderingDbContext>();
        var tenant = ServiceFactory<OrderingService, OrderingDbContext>.DefaultTenantId;
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        var order = new Order(tenant, f.Branches.Add(tenant), OrderType.DineIn, DateTime.UtcNow);
        order.AttachToTableSession(Guid.NewGuid());
        order.AddItem(Guid.NewGuid(), "Coffee", 35000, 2);
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        var v1 = AdmissionProjection.New(order, DateTime.UtcNow);
        AdmissionProjection.Request(v1, order).Source.PayFirst.Should().BeTrue();
        db.Entry(order).Property(o => o.FulfillmentMode).CurrentValue = FulfillmentModes.CupV1;
        var v2 = AdmissionProjection.New(order, DateTime.UtcNow);
        AdmissionProjection.CupRequest(v2, order).Source.PayFirst.Should().BeTrue();
    }

    [Fact]
    public async Task Cup_QR_cannot_start_until_payment_and_replay_preserves_identity()
    {
        using var f = new ServiceFactory<KitchenOperationsService, KitchenOperationsDbContext>();
        var tenant = ServiceFactory<KitchenOperationsService, KitchenOperationsDbContext>.DefaultTenantId;
        var branch = f.Branches.Add(tenant);
        var request = Request(tenant, branch);
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KitchenOperationsDbContext>();
        var writer = new CupAdmissionWriter(db, f.Clock);
        var ack = await writer.ApplyAsync(request);
        var ticket = await db.Tickets.SingleAsync();
        ticket.PayFirst.Should().BeTrue();
        ticket.IsEligibleForBar.Should().BeFalse();
        var cup = await db.CupTasks.OrderBy(c => c.OrderCupIndex).FirstAsync();
        var operations = new CupServingUnitOfWork(db, new Barista(tenant, branch), f.Clock, TimeSpan.FromMinutes(1));
        await Assert.ThrowsAsync<ConflictException>(() => operations.ExecuteAsync(new(cup.Id, "start", CupAction.Start, 0, 1)));
        await f.HandleAsync(new PaymentSucceededEvent(tenant, branch, Guid.NewGuid(), request.OrderId, 70000, "Cash", DateTime.UtcNow));
        // Each HTTP operation has a fresh unit of work; discard the failed request's tracking state.
        db.ChangeTracker.Clear();
        ticket = await db.Tickets.SingleAsync();
        ticket.IsEligibleForBar.Should().BeTrue();
        (await operations.ExecuteAsync(new(cup.Id, "start", CupAction.Start, 0, 1))).Cup.Preparation.Should().Be(CupPreparation.Brewing);
        var replay = await writer.ApplyAsync(request);
        replay.TicketId.Should().Be(ack.TicketId);
        (await db.CupTasks.CountAsync()).Should().Be(2);
        (await db.CupStickers.CountAsync()).Should().Be(2);
        (await db.AdmissionReceipts.CountAsync()).Should().Be(1);
        (await db.CupTasks.SingleAsync(c => c.Id == cup.Id)).Preparation.Should().Be(CupPreparation.Brewing);
    }

    [Fact]
    public async Task Early_payment_is_consumed_by_V2_and_duplicate_delivery_does_not_reset_cups()
    {
        using var f = new ServiceFactory<KitchenOperationsService, KitchenOperationsDbContext>();
        var tenant = ServiceFactory<KitchenOperationsService, KitchenOperationsDbContext>.DefaultTenantId;
        var branch = f.Branches.Add(tenant);
        var request = Request(tenant, branch);
        var payment = new PaymentSucceededEvent(tenant, branch, Guid.NewGuid(), request.OrderId, 70000, "Cash", DateTime.UtcNow);
        await f.HandleAsync(payment);
        await f.HandleAsync(payment);
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KitchenOperationsDbContext>();
        (await db.EarlyPayments.CountAsync()).Should().Be(1);
        var writer = new CupAdmissionWriter(db, f.Clock);
        var ack = await writer.ApplyAsync(request);
        (await db.Tickets.SingleAsync()).IsPaid.Should().BeTrue();
        (await db.EarlyPayments.CountAsync()).Should().Be(0);
        (await writer.ApplyAsync(request)).TicketId.Should().Be(ack.TicketId);
        await f.HandleAsync(payment);
        (await db.CupTasks.CountAsync()).Should().Be(2);
        (await db.CupStickers.CountAsync()).Should().Be(2);
        (await db.AdmissionReceipts.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Replay_cannot_clear_payment_first_policy_or_recovery_hold()
    {
        using var f = new ServiceFactory<KitchenOperationsService, KitchenOperationsDbContext>();
        var tenant = ServiceFactory<KitchenOperationsService, KitchenOperationsDbContext>.DefaultTenantId;
        var request = Request(tenant, f.Branches.Add(tenant));
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KitchenOperationsDbContext>();
        var writer = new CupAdmissionWriter(db, f.Clock);
        await writer.ApplyAsync(request);
        var replay = await writer.ApplyAsync(request with { Source = request.Source with { PayFirst = false } });
        replay.ReceiptStatus.Should().Be("NeedsReview");
        var ticket = await db.Tickets.SingleAsync();
        ticket.PayFirst.Should().BeTrue();
        ticket.IsEligibleForBar.Should().BeFalse();
        ticket.RecoveryHoldReason.Should().NotBeNull();
        (await writer.ApplyAsync(request)).ReceiptStatus.Should().Be("NeedsReview");
    }
}
