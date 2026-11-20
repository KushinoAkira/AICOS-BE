using System.Security.Cryptography;
using System.Text.Json;

namespace AICOS.Contracts.Orders;

public sealed record CancellationReservation(Guid OperationId, Guid TenantId, Guid BranchId, Guid OrderId,
    Guid ActorId, string OrderCode, string TableCode, string SourceFingerprint);
public sealed record CancellationAck(Guid OperationId, Guid TenantId, Guid BranchId, Guid OrderId,
    string RequestHash, string State, string? ReasonCode, Guid? TicketId);

public static class CancellationProtocol
{
    public static string Hash(CancellationReservation request) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request)));
}
