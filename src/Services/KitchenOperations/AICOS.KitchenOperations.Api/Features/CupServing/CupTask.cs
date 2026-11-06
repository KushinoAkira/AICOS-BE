using AICOS.BuildingBlocks.Exceptions;
using AICOS.Contracts.Serving;

namespace AICOS.KitchenOperations.Api.Features.CupServing;

public enum CupPreparation { Pending, Brewing, Ready }
public enum CupService { Waiting, Available, Reserved, Carrying, Served, Review }

/// <summary>One physical serving unit. Methods are persisted with its claim/ledger/outbox under the order lock.</summary>
public sealed class CupTask
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid BranchId { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid TicketId { get; private set; }
    public Guid TicketItemId { get; private set; }
    public Guid OrderItemId { get; private set; }
    public int UnitIndex { get; private set; }
    public int OrderCupIndex { get; private set; }
    public CupPreparation Preparation { get; private set; }
    public CupService Service { get; private set; }
    public int PreparationCycle { get; private set; } = 1;
    public long Version { get; private set; }
    public long Fence { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? ReadyAtUtc { get; private set; }
    public Guid? PreparedBy { get; private set; }
    public DateTime? PickedUpAtUtc { get; private set; }
    public DateTime? ServedAtUtc { get; private set; }
    public Guid? ServedBy { get; private set; }
    public string? ReviewReason { get; private set; }

    private CupTask() { }

    public CupTask(Guid id, Guid tenant, Guid branch, Guid order, Guid ticket, Guid ticketItem,
        Guid orderItem, int unitIndex, int orderCupIndex, DateTime now)
    {
        if (new[] { id, tenant, branch, order, ticket, ticketItem, orderItem }.Contains(Guid.Empty)
            || unitIndex < 1 || orderCupIndex < 1 || now.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Cup identity, positive ordinals and UTC time are required.");
        if (id != CupIdentity.Create(tenant, order, orderItem, unitIndex))
            throw new ArgumentException("Cup ID must match its stable source identity.");
        Id = id; TenantId = tenant; BranchId = branch; OrderId = order; TicketId = ticket;
        TicketItemId = ticketItem; OrderItemId = orderItem; UnitIndex = unitIndex;
        OrderCupIndex = orderCupIndex; CreatedAtUtc = now;
    }

    public void Check(long version, int cycle)
    {
        if (Version != version || PreparationCycle != cycle)
            throw new ConflictException("Reload the cup before acting.", "serving.stale");
        if (ReviewReason is not null)
            throw new ConflictException("Physical review is required.", "serving.review_required");
    }

    public void Prepare(CupPreparation target, Guid actor, DateTime now)
    {
        RequireActor(actor);
        if (ReviewReason is not null || Service != CupService.Waiting
            || !((Preparation == CupPreparation.Pending && target == CupPreparation.Brewing)
                || (Preparation == CupPreparation.Brewing && target == CupPreparation.Ready)))
            throw new ConflictException("Invalid preparation transition.", "serving.invalid_preparation");
        Preparation = target; PreparedBy = actor; Version++;
        if (target == CupPreparation.Ready) { Service = CupService.Available; ReadyAtUtc = now; }
    }

    public CupClaim Reserve(Guid actor, DateTime now, DateTime expires)
    {
        RequireActor(actor);
        if (ReviewReason is not null || Preparation != CupPreparation.Ready || Service != CupService.Available)
            throw new ConflictException("This cup is not available.", "serving.claimed");
        if (expires <= now) throw new ArgumentException("Lease must end in the future.");
        Fence++; Version++; Service = CupService.Reserved;
        return new CupClaim(this, actor, now, expires);
    }

    public void Pickup(CupClaim claim, Guid actor, DateTime now)
    {
        RequireClaim(claim, actor, now);
        if (Service != CupService.Reserved || claim.State != CupClaimState.Reserved)
            throw new ConflictException("Cup has already left the bar or changed.", "serving.invalid_pickup");
        claim.Pickup(now); Service = CupService.Carrying; PickedUpAtUtc = now; Version++;
    }

    public void Serve(CupClaim claim, Guid actor, DateTime now)
    {
        RequireClaim(claim, actor, now);
        if (Service != CupService.Carrying || claim.State != CupClaimState.Carrying)
            throw new ConflictException("Pickup must be confirmed first.", "serving.invalid_serve");
        claim.Finish(CupClaimState.Served, now); Service = CupService.Served;
        ServedAtUtc = now; ServedBy = actor; Version++;
    }

    public void Release(CupClaim claim, Guid actor, DateTime now)
    {
        RequireClaim(claim, actor, now);
        if (Service != CupService.Reserved || claim.State != CupClaimState.Reserved)
            throw new ConflictException("A carried cup requires physical review.", "serving.review_required");
        claim.Finish(CupClaimState.Released, now); Service = CupService.Available; Fence++; Version++;
    }

    public void Renew(CupClaim claim, Guid actor, DateTime now, DateTime expires)
    {
        RequireClaim(claim, actor, now);
        claim.Renew(expires); Version++;
    }

    /// <summary>Persisted expiry never releases custody, even before pickup.</summary>
    public bool MarkOverdue(CupClaim claim, DateTime now)
    {
        if (claim.CupId != Id || claim.Fence != Fence || claim.PreparationCycle != PreparationCycle)
            throw new ConflictException("Claim no longer belongs to this cup cycle.", "serving.stale");
        if (!claim.IsLive || claim.State == CupClaimState.Overdue || claim.LeaseExpiresAtUtc > now) return false;
        claim.Overdue(); ReviewReason = "serving.claim_overdue"; Service = CupService.Review; Version++;
        return true;
    }

    private void RequireClaim(CupClaim claim, Guid actor, DateTime now)
    {
        RequireActor(actor);
        if (claim.TenantId != TenantId || claim.BranchId != BranchId)
            throw new ConflictException("Claim scope differs from the cup.", "serving.identity_conflict");
        if (claim.CupId != Id || claim.OwnerUserId != actor)
            throw new ForbiddenException("Only the claim owner can move this cup.");
        if (claim.Fence != Fence || claim.PreparationCycle != PreparationCycle || !claim.IsLive)
            throw new ConflictException("The claim is fenced or ended.", "serving.stale");
        if (ReviewReason is not null || claim.State == CupClaimState.Overdue || claim.LeaseExpiresAtUtc <= now)
            throw new ConflictException("The claim needs physical review.", "serving.lease_expired");
    }

    private static void RequireActor(Guid actor)
    {
        if (actor == Guid.Empty) throw new ArgumentException("Actor must come from the authenticated user.");
    }
}
