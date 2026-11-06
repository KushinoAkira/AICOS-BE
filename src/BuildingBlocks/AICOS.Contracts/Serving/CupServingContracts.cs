using System.Security.Cryptography;
using System.Text.Json;

namespace AICOS.Contracts.Serving;

public static class FulfillmentModes
{
    public const string WholeOrder = "WholeOrder";
    public const string CupV1 = "CupV1";
}

/// <summary>Nonfinancial, versioned snapshot. Not a payment, delivery or session-close command.</summary>
public sealed record FulfillmentSnapshot(Guid EventId, Guid TenantId, Guid BranchId, Guid OrderId,
    Guid TicketId, long Version, string ManifestHash, int Total, int Waiting, int Available,
    int Reserved, int Carrying, int Served, int Review, DateTime? FulfilledAtUtc);

public sealed record CupExpectation(long Version, int PreparationCycle);
public sealed record ClaimExpectation(long Version, int PreparationCycle, Guid ClaimId, long Fence);
public sealed record CupServingCapabilities(int CupServingVersion, int ClaimVersion, bool Enabled);
public sealed record FulfillmentAck(Guid EventId, Guid TenantId, Guid BranchId, Guid OrderId,
    long Version, string PayloadHash, string Outcome);

public static class FulfillmentSnapshotHash
{
    // Event identity is separate from the immutable effect at a source version.
    public static string Compute(FulfillmentSnapshot snapshot) => Convert.ToHexString(
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(snapshot with { EventId = Guid.Empty })));
}
