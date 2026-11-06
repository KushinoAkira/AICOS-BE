namespace AICOS.KitchenOperations.Api.Features.CupServing;

/// <summary>Kitchen-owned durable summary. ACK is a downstream DB commit, never Redis publication.</summary>
public sealed class FulfillmentOutbox
{
    public Guid EventId { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid BranchId { get; set; }
    public Guid OrderId { get; set; }
    public long SourceVersion { get; set; }
    public string Payload { get; set; } = "{}";
    public string PayloadHash { get; set; } = "";
    public string State { get; set; } = "PENDING";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime NextAttemptAtUtc { get; set; }
    public int AttemptCount { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTime? LeaseUntilUtc { get; set; }
    public DateTime? AcknowledgedAtUtc { get; set; }
    public string? LastErrorCode { get; set; }
}
