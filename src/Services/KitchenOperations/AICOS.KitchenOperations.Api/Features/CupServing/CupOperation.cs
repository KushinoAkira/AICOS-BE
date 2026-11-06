namespace AICOS.KitchenOperations.Api.Features.CupServing;

/// <summary>One committed effect: append-only audit and idempotency result in the same row.</summary>
public sealed class CupOperation
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid TenantId { get; init; }
    public Guid BranchId { get; init; }
    public Guid CupId { get; init; }
    public Guid ActorId { get; init; }
    public string OperationKey { get; init; } = "";
    public string Command { get; init; } = "";
    public string RequestHash { get; init; } = "";
    public long VersionBefore { get; init; }
    public long VersionAfter { get; init; }
    public string BeforeJson { get; init; } = "{}";
    public string AfterJson { get; init; } = "{}";
    public string ResultJson { get; init; } = "{}";
    public string? Reason { get; init; }
    public DateTime CommittedAtUtc { get; init; }
}
