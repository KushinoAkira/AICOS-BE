using System.Net.Http.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Billing;
namespace AICOS.Payments.Api.Features.SessionBilling;
public interface IBillKitchenClient { Task<bool> PaidAsync(KitchenBillPaidNotice notice, CancellationToken ct); }
internal sealed class HttpBillKitchenClient(HttpClient http, IConfiguration config) : IBillKitchenClient
{
    public async Task<bool> PaidAsync(KitchenBillPaidNotice notice, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/session-billing/paid") { Content = JsonContent.Create(notice) };
        request.Headers.Add(BillingMachineAuth.Header, BillingMachineAuth.Key(config));
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return false;
        var ack = await response.Content.ReadFromJsonAsync<Ack>(cancellationToken: ct);
        return ack is not null && ack.Applied && ack.BillId == notice.Payment.BillId && ack.PaymentId == notice.Payment.PaymentId;
    }
    private sealed record Ack(Guid BillId, Guid PaymentId, bool Applied);
}
