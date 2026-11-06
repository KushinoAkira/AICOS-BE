namespace AICOS.KitchenOperations.Api.Features.CupServing;

public enum CupClaimState { Reserved, Carrying, Overdue, Served, Released, Overridden }

public sealed class CupClaim
{
    public Guid Id { get; private set; }
    public Guid CupId { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid BranchId { get; private set; }
    public Guid OwnerUserId { get; private set; }
    public int PreparationCycle { get; private set; }
    public long Fence { get; private set; }
    public long Version { get; private set; }
    public CupClaimState State { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime LeaseExpiresAtUtc { get; private set; }
    public DateTime? PickedUpAtUtc { get; private set; }
    public DateTime? EndedAtUtc { get; private set; }
    public bool IsLive => State is CupClaimState.Reserved or CupClaimState.Carrying or CupClaimState.Overdue;

    private CupClaim() { }
    internal CupClaim(CupTask cup, Guid owner, DateTime now, DateTime expires)
    {
        Id = Guid.NewGuid(); CupId = cup.Id; TenantId = cup.TenantId; BranchId = cup.BranchId;
        OwnerUserId = owner; PreparationCycle = cup.PreparationCycle; Fence = cup.Fence;
        CreatedAtUtc = now; LeaseExpiresAtUtc = expires;
    }

    internal void Pickup(DateTime now) { State = CupClaimState.Carrying; PickedUpAtUtc = now; Version++; }
    internal void Finish(CupClaimState state, DateTime now) { State = state; EndedAtUtc = now; Version++; }
    internal void Overdue() { State = CupClaimState.Overdue; Version++; }
    internal void Renew(DateTime expires)
    {
        if (expires <= LeaseExpiresAtUtc) throw new ArgumentException("Renewal must extend the current lease.");
        LeaseExpiresAtUtc = expires; Version++;
    }
}
