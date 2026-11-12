using System.Security.Cryptography;
using System.Text.Json;

namespace AICOS.Contracts.KitchenAdmission;

public sealed record AdmissionItem(Guid ProductId, string ProductName, int Quantity, string? Note);
public sealed record AdmissionCreation(string OrderCode, IReadOnlyList<AdmissionItem> Items);
public sealed record AdmissionFacts(string OrderType, string Status, bool IsPaid, string? TableNumber,
    DateTime CreatedAtUtc, DateTime? PickupSlotStartUtc, DateTime? DeliveredAtUtc, DateTime? CancelledAtUtc, bool PayFirst = false);
public sealed record AdmissionRequest(Guid EventId, int SchemaVersion, string ConsumerKey, Guid TenantId,
    Guid BranchId, Guid OrderId, AdmissionCreation Creation, string CreationHash, AdmissionFacts Source);
public sealed record AdmissionAck(Guid EventId, string ConsumerKey, Guid TenantId, Guid BranchId,
    Guid OrderId, Guid TicketId, string Result, string ReceiptStatus);
public sealed record AdmissionLookup(Guid TenantId, Guid BranchId, IReadOnlyList<Guid> OrderIds);
public sealed record AdmissionPresence(Guid OrderId, Guid? TicketId, string? Status, string? Hold,
    string? TableNumber, DateTime? DeliveredAtUtc, bool? IsPaid = null);

public static class AdmissionProtocol
{
    public const string Consumer = "kitchen-order-admission-v1";
    public const string Producer = "ordering";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static byte[] Hash(Guid tenant, Guid branch, Guid order, AdmissionCreation creation)
    {
        var items = creation.Items.Select(i => JsonSerializer.Serialize(i, Json)).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        return SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { tenant, branch, order, creation.OrderCode, items }, Json));
    }
}
