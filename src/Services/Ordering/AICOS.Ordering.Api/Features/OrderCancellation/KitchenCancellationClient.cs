using System.Net.Http.Json;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Orders;

namespace AICOS.Ordering.Api.Features.OrderCancellation;

public interface IKitchenCancellationClient
{
    Task<CancellationAck> SendAsync(string action, CancellationReservation request, CancellationToken ct);
}

public sealed class KitchenCancellationClient(HttpClient http, IConfiguration config) : IKitchenCancellationClient
{
    public async Task<CancellationAck> SendAsync(string action, CancellationReservation r, CancellationToken ct)
    {
        if (action is not ("reserve" or "commit" or "abort")) throw new ArgumentOutOfRangeException(nameof(action));
        using var message = new HttpRequestMessage(HttpMethod.Post, $"/internal/order-cancellations/{action}") { Content = JsonContent.Create(r) };
        message.Headers.Add(OrderCancellationMachineAuth.Header, OrderCancellationMachineAuth.Key(config));
        using var response = await http.SendAsync(message, ct);
        if (!response.IsSuccessStatusCode) throw new DependencyUnavailableException("kitchen-cancellation");
        var ack = await response.Content.ReadFromJsonAsync<CancellationAck>(cancellationToken: ct);
        if (ack is null || ack.OperationId != r.OperationId || ack.OrderId != r.OrderId || ack.TenantId != r.TenantId
            || ack.BranchId != r.BranchId || ack.RequestHash != CancellationProtocol.Hash(r)
            || ack.State is not ("HELD" or "WAITING" or "CANCELLED" or "ABORTED" or "REJECTED"))
            throw new DependencyUnavailableException("kitchen-cancellation-ack");
        return ack;
    }
}
