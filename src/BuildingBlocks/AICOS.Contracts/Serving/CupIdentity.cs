using System.Security.Cryptography;
using System.Text;

namespace AICOS.Contracts.Serving;

public static class CupIdentity
{
    // Frozen UUIDv5 namespace for logical cups. Identity is not an authorization credential.
    private static readonly Guid NamespaceId = new("db7b39aa-197f-4a93-85dc-01f58a01d621");

    public static Guid Create(Guid tenant, Guid order, Guid orderItem, int unitIndex)
    {
        if (new[] { tenant, order, orderItem }.Contains(Guid.Empty) || unitIndex < 1)
            throw new ArgumentException("Stable source IDs and a positive ordinal are required.");
        var name = Encoding.UTF8.GetBytes(FormattableString.Invariant($"{tenant:D}/{order:D}/{orderItem:D}/{unitIndex}"));
        var input = NamespaceId.ToByteArray(bigEndian: true).Concat(name).ToArray();
        var hash = SHA1.HashData(input); // UUIDv5 naming only, never signing, passwords or integrity.
        hash[6] = (byte)((hash[6] & 0x0f) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }
}
