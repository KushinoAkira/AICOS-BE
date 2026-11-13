using System.Net.Http.Json;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Billing;
namespace AICOS.Ordering.Api.Features.SessionBilling;
public interface IBillSettlementClient
{
    Task<BillCloseProof?> ProofAsync(Guid billId, CancellationToken ct);
    Task<BillCloseProof> SealAsync(BillCloseProof proof, CancellationToken ct);
}
internal sealed class HttpBillSettlementClient(HttpClient http, IConfiguration config) : IBillSettlementClient
{
    public async Task<BillCloseProof?> ProofAsync(Guid billId, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, $"/internal/session-billing/bills/{billId}/proof");
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<BillCloseProof>(cancellationToken: ct);
    }
    public async Task<BillCloseProof> SealAsync(BillCloseProof proof, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Post, $"/internal/session-billing/bills/{proof.BillId}/seal");
        request.Content = JsonContent.Create(new BillSealRequest(proof));
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new ConflictException("Settlement changed or could not be verified. Do not collect again.", "billing.not_settled");
        var ack = await response.Content.ReadFromJsonAsync<BillCloseProof>(cancellationToken: ct);
        if (ack != proof) throw new ConflictException("Close acknowledgement differs.", "billing.stale");
        return ack;
    }
    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path); request.Headers.Add(BillingMachineAuth.Header, BillingMachineAuth.Key(config)); return request;
    }
}
