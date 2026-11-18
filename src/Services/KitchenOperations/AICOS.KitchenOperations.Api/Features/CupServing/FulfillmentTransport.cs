using System.Net.Http.Headers;
using System.Net.Http.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Serving;

namespace AICOS.KitchenOperations.Api.Features.CupServing;

public sealed record FulfillmentTransportResult(int Status, FulfillmentAck? Ack);
public interface IFulfillmentTransport
{
    Task<FulfillmentTransportResult> SendAsync(FulfillmentSnapshot snapshot, CancellationToken ct);
}

public sealed class HttpFulfillmentTransport(HttpClient client, IConfiguration config) : IFulfillmentTransport
{
    public async Task<FulfillmentTransportResult> SendAsync(FulfillmentSnapshot snapshot, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/ordering/cup-fulfillment")
        { Content = JsonContent.Create(snapshot) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
            FulfillmentMachineAuthentication.Token(FulfillmentMachineAuthentication.Key(config), snapshot.TenantId, snapshot.BranchId));
        using var response = await client.SendAsync(request, ct);
        var ack = response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<FulfillmentAck>(ct) : null;
        return new((int)response.StatusCode, ack);
    }
}
