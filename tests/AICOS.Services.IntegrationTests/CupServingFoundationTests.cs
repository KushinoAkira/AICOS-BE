using AICOS.BuildingBlocks.Exceptions;
using AICOS.Contracts.Serving;
using AICOS.KitchenOperations.Api.Features.CupServing;
using AICOS.Ordering.Api.Features.Fulfillment;

namespace AICOS.Services.IntegrationTests;

/// <summary>Pure domain/contract tests, NOT PostgreSQL concurrency or runnable public Serving API.</summary>
public sealed class CupServingFoundationTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Branch = Guid.NewGuid(), Actor = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 11, 7, 8, 0, 0, DateTimeKind.Utc);

    private static CupTask Cup(Guid? order = null, Guid? item = null, int ordinal = 1)
    {
        var orderId = order ?? Guid.NewGuid(); var itemId = item ?? Guid.NewGuid();
        return new CupTask(CupIdentity.Create(Tenant, orderId, itemId, ordinal), Tenant, Branch,
            orderId, Guid.NewGuid(), Guid.NewGuid(), itemId, ordinal, ordinal, Now);
    }
    private static CupTask Ready()
    {
        var cup = Cup(); cup.Prepare(CupPreparation.Brewing, Actor, Now);
        cup.Prepare(CupPreparation.Ready, Actor, Now); return cup;
    }

    [Fact]
    public void Cup_identity_has_a_frozen_cross_runtime_golden_vector()
    {
        CupIdentity.Create(Tenant, Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Guid.Parse("33333333-3333-3333-3333-333333333333"), 1)
            .Should().Be(Guid.Parse("8cfe8b2f-1cbc-5f02-afc0-fddb61e1229e"));
    }

    [Fact]
    public void Stable_ids_distinguish_identical_units_lines_and_tenants()
    {
        var order = Guid.NewGuid(); var line = Guid.NewGuid();
        var id = CupIdentity.Create(Tenant, order, line, 1);
        id.Should().Be(CupIdentity.Create(Tenant, order, line, 1));
        new[] { id, CupIdentity.Create(Tenant, order, line, 2), CupIdentity.Create(Tenant, order, Guid.NewGuid(), 1),
            CupIdentity.Create(Guid.NewGuid(), order, line, 1) }.Should().OnlyHaveUniqueItems();
        id.ToString()[14].Should().Be('5');
    }

    [Fact]
    public void Two_identical_cups_can_be_served_by_different_staff_independently()
    {
        var order = Guid.NewGuid(); var line = Guid.NewGuid();
        var a = Cup(order, line, 1); var b = Cup(order, line, 2); var other = Guid.NewGuid();
        foreach (var cup in new[] { a, b }) { cup.Prepare(CupPreparation.Brewing, Actor, Now); cup.Prepare(CupPreparation.Ready, Actor, Now); }
        var ca = a.Reserve(Actor, Now, Now.AddMinutes(1)); var cb = b.Reserve(other, Now, Now.AddMinutes(1));
        a.Pickup(ca, Actor, Now); a.Serve(ca, Actor, Now);
        a.Service.Should().Be(CupService.Served); b.Service.Should().Be(CupService.Reserved);
        b.Pickup(cb, other, Now); b.Serve(cb, other, Now);
        a.ServedBy.Should().Be(Actor); b.ServedBy.Should().Be(other);
    }

    [Fact]
    public void A_second_claim_or_duplicate_serve_cannot_repeat_the_effect()
    {
        var cup = Ready(); var claim = cup.Reserve(Actor, Now, Now.AddMinutes(1));
        Action second = () => cup.Reserve(Guid.NewGuid(), Now, Now.AddMinutes(1));
        second.Should().Throw<ConflictException>();
        Action early = () => cup.Serve(claim, Actor, Now);
        early.Should().Throw<ConflictException>();
        cup.Pickup(claim, Actor, Now); cup.Serve(claim, Actor, Now);
        var version = cup.Version;
        Action again = () => cup.Serve(claim, Actor, Now);
        again.Should().Throw<ConflictException>(); cup.Version.Should().Be(version);
    }

    [Fact]
    public void Wrong_owner_and_stale_version_or_cycle_are_rejected()
    {
        var cup = Ready(); var observed = cup.Version; var claim = cup.Reserve(Actor, Now, Now.AddMinutes(1));
        Action wrong = () => cup.Pickup(claim, Guid.NewGuid(), Now);
        wrong.Should().Throw<ForbiddenException>();
        Action stale = () => cup.Check(observed, 1); stale.Should().Throw<ConflictException>();
        Action cycle = () => cup.Check(cup.Version, 2); cycle.Should().Throw<ConflictException>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Timeout_never_releases_custody_even_before_pickup(bool carrying)
    {
        var cup = Ready(); var claim = cup.Reserve(Actor, Now, Now.AddMinutes(1));
        if (carrying) cup.Pickup(claim, Actor, Now);
        cup.MarkOverdue(claim, Now.AddMinutes(1)).Should().BeTrue();
        cup.Service.Should().Be(CupService.Review); claim.State.Should().Be(CupClaimState.Overdue);
        claim.IsLive.Should().BeTrue(); cup.ServedAtUtc.Should().BeNull();
        cup.MarkOverdue(claim, Now.AddMinutes(2)).Should().BeFalse();
        Action reserve = () => cup.Reserve(Guid.NewGuid(), Now.AddMinutes(2), Now.AddMinutes(3));
        reserve.Should().Throw<ConflictException>();
        Action serve = () => cup.Serve(claim, Actor, Now.AddMinutes(2)); serve.Should().Throw<ConflictException>();
    }

    [Fact]
    public void Carried_cup_cannot_be_released_and_old_claim_is_fenced_after_safe_release()
    {
        var carried = Ready(); var carrying = carried.Reserve(Actor, Now, Now.AddMinutes(1));
        carried.Pickup(carrying, Actor, Now);
        Action unsafeRelease = () => carried.Release(carrying, Actor, Now); unsafeRelease.Should().Throw<ConflictException>();
        var cup = Ready(); var old = cup.Reserve(Actor, Now, Now.AddMinutes(1)); cup.Release(old, Actor, Now);
        var next = cup.Reserve(Actor, Now, Now.AddMinutes(1)); next.Fence.Should().BeGreaterThan(old.Fence);
        Action stale = () => cup.Pickup(old, Actor, Now); stale.Should().Throw<ConflictException>();
    }

    [Fact]
    public void Expired_claim_is_blocked_even_when_timeout_worker_has_not_run()
    {
        var cup = Ready(); var claim = cup.Reserve(Actor, Now, Now.AddSeconds(1));
        Action pickup = () => cup.Pickup(claim, Actor, Now.AddSeconds(1)); pickup.Should().Throw<ConflictException>();
        Action renew = () => cup.Renew(claim, Actor, Now.AddSeconds(1), Now.AddMinutes(2)); renew.Should().Throw<ConflictException>();
        claim.IsLive.Should().BeTrue(); cup.Service.Should().Be(CupService.Reserved);
    }

    private static FulfillmentSnapshot Snapshot(long version = 1) => new(Guid.NewGuid(), Tenant, Branch,
        Guid.NewGuid(), Guid.NewGuid(), version, new string('A', 64), 3, 2, 1, 0, 0, 0, 0, null);

    [Fact]
    public void Projection_dedupes_same_effect_and_refuses_conflicting_version_or_identity()
    {
        var p = new OrderFulfillmentProjection(); var s = Snapshot();
        p.Apply(s, Now).Should().Be(SnapshotOutcome.Applied);
        p.Apply(s with { EventId = Guid.NewGuid() }, Now.AddMinutes(1)).Should().Be(SnapshotOutcome.Duplicate);
        Action conflict = () => p.Apply(s with { Waiting = 1, Available = 2 }, Now);
        conflict.Should().Throw<ConflictException>();
        Action scope = () => p.Apply(s with { Version = 2, TenantId = Guid.NewGuid() }, Now);
        scope.Should().Throw<ConflictException>();
    }

    [Fact]
    public void Full_snapshot_accepts_version_gaps_and_never_regresses_served_or_final_state()
    {
        var p = new OrderFulfillmentProjection(); var s = Snapshot(); p.Apply(s, Now);
        var partial = s with { Version = 8, Waiting = 1, Served = 1 };
        p.Apply(partial, Now).Should().Be(SnapshotOutcome.Applied);
        p.Apply(s, Now).Should().Be(SnapshotOutcome.Superseded); p.Served.Should().Be(1);
        Action regress = () => p.Apply(s with { Version = 9 }, Now); regress.Should().Throw<ConflictException>();
        p.Apply(s with { Version = 10, Waiting = 0, Available = 0, Served = 3, FulfilledAtUtc = Now }, Now);
        Action reopen = () => p.Apply(partial with { Version = 11 }, Now); reopen.Should().Throw<ConflictException>();
        p.FulfilledAtUtc.Should().Be(Now);
    }

    [Theory]
    [InlineData(-1, 4)]
    [InlineData(1, 1)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void Invalid_or_overflowing_counts_are_rejected(int waiting, int available)
    {
        var s = Snapshot() with { Waiting = waiting, Available = available };
        Action apply = () => new OrderFulfillmentProjection().Apply(s, Now);
        apply.Should().Throw<ValidationFailedException>();
    }
}
