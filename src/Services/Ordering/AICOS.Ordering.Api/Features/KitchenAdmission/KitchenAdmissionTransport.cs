using System.Net.Http.Headers;
using System.Net.Http.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.KitchenAdmission;

namespace AICOS.Ordering.Api.Features.KitchenAdmission;

public sealed record AdmissionTransportResult(int Status, AdmissionAck? Ack);
public interface IKitchenAdmissionTransport
{
    Task<AdmissionTransportResult> SendAsync(AdmissionRequest request, CancellationToken ct);
    Task<IReadOnlyList<AdmissionPresence>> LookupAsync(Guid tenant, Guid branch, IReadOnlyList<Guid> orders, CancellationToken ct);
}

public sealed class HttpKitchenAdmissionTransport(HttpClient client, IConfiguration configuration) : IKitchenAdmissionTransport
{
    private HttpRequestMessage Request(string path, Guid tenant, Guid branch, string scope, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: AdmissionProtocol.Json) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AdmissionMachineAuthentication.Token(
            AdmissionMachineAuthentication.RequireKey(configuration), tenant, branch, scope, DateTime.UtcNow));
        return request;
    }

    public async Task<AdmissionTransportResult> SendAsync(AdmissionRequest request, CancellationToken ct)
    {
        using var message = Request("/internal/v1/kitchen/order-admissions", request.TenantId, request.BranchId, "kitchen.admission.write", request);
        using var response = await client.SendAsync(message, ct);
        AdmissionAck? ack = null;
        if (response.IsSuccessStatusCode)
        {
            try { ack = await response.Content.ReadFromJsonAsync<AdmissionAck>(AdmissionProtocol.Json, ct); }
            catch (System.Text.Json.JsonException) { return new(502, null); }
        }
        return new((int)response.StatusCode, ack);
    }

    public async Task<IReadOnlyList<AdmissionPresence>> LookupAsync(Guid tenant, Guid branch, IReadOnlyList<Guid> orders, CancellationToken ct)
    {
        using var request = Request("/internal/v1/kitchen/order-admissions/lookup", tenant, branch, "kitchen.admission.lookup", new AdmissionLookup(tenant, branch, orders));
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<List<AdmissionPresence>>(AdmissionProtocol.Json, ct)
            ?? throw new HttpRequestException("Invalid admission lookup.");
        if (result.Count != orders.Distinct().Count() || result.Select(r => r.OrderId).Distinct().Count() != result.Count
            || result.Any(r => !orders.Contains(r.OrderId))) throw new HttpRequestException("Admission lookup identity mismatch.");
        return result;
    }
}
