using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace AICOS.Contracts.Billing;

public sealed record BillLine(Guid OrderId, string OrderCode, string Name, int Quantity, decimal UnitPrice, decimal Total, string Options, string? Note);
public sealed record BillRound(Guid OrderId, string OrderCode, decimal Amount);
public sealed record SessionBillQuote(Guid TenantId, Guid BranchId, Guid SessionId, Guid TableId, string TableCode,
    string Fingerprint, decimal Subtotal, decimal Discount, decimal Total, IReadOnlyList<BillRound> Orders, IReadOnlyList<BillLine> Lines, decimal Tax = 0, decimal ServiceFee = 0);
public sealed record FreezeBillRequest(Guid TenantId, Guid BranchId, Guid BillId, string Fingerprint);
public sealed record BillPaidNotice(Guid TenantId, Guid BranchId, Guid SessionId, Guid BillId, string Fingerprint,
    Guid PaymentId, string Method, DateTime PaidAtUtc);
public sealed record BillCloseProof(Guid TenantId, Guid BranchId, Guid SessionId, Guid BillId, Guid PaymentId, string Fingerprint);
public sealed record BillSealRequest(BillCloseProof Proof);
public static class BillIdentity
{
    public static Guid For(Guid tenant, Guid session) => new(SHA256.HashData(Encoding.UTF8.GetBytes($"aicos:session-bill:v1:{tenant}:{session}"))[..16]);
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    public static string Fingerprint(IEnumerable<BillRound> orders) => Hash(JsonSerializer.Serialize(orders.OrderBy(o => o.OrderId)));
}

public sealed record KitchenBillPaidNotice(BillPaidNotice Payment, IReadOnlyList<Guid> OrderIds);
