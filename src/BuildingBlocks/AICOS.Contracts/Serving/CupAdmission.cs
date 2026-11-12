using System.Security.Cryptography;
using System.Text.Json;
using AICOS.Contracts.KitchenAdmission;

namespace AICOS.Contracts.Serving;

public sealed record CupAdmissionItem(Guid OrderItemId, Guid ProductId, string ProductName, int Quantity, string? Note);
public sealed record CupManifest(string OrderCode, string FulfillmentMode, Guid? TableId, Guid? SessionId,
    IReadOnlyList<CupAdmissionItem> Items);
public sealed record CupAdmissionRequest(Guid EventId, Guid TenantId, Guid BranchId, Guid OrderId,
    CupManifest Manifest, string ManifestHash, AdmissionFacts Source);

public static class CupAdmissionProtocol
{
    public const string Consumer = "kitchen-order-admission-v2";
    public static string Hash(Guid tenant, Guid branch, Guid order, CupManifest manifest) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            tenant,
            branch,
            order,
            manifest.OrderCode,
            manifest.FulfillmentMode,
            manifest.TableId,
            manifest.SessionId,
            items = manifest.Items.OrderBy(i => i.OrderItemId).ToArray(),
        }, AdmissionProtocol.Json)));
}
