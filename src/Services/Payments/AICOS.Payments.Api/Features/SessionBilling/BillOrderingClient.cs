using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Billing;
namespace AICOS.Payments.Api.Features.SessionBilling;

public interface IBillOrderingClient
{
    Task<SessionBillQuote> QuoteAsync(Guid session, CancellationToken ct);
    Task<SessionBillQuote> FreezeAsync(Guid session, FreezeBillRequest request, CancellationToken ct);
    Task<bool> PaidAsync(BillPaidNotice notice, CancellationToken ct);
}
internal sealed class HttpBillOrderingClient(HttpClient http, IConfiguration config) : IBillOrderingClient
{
    public async Task<SessionBillQuote> QuoteAsync(Guid session, CancellationToken ct)
    {
        using var response = await http.GetAsync($"/api/v1/orders/qr/sessions/{session}/billing-quote", ct);
        return await Read(response, ct);
    }
    public async Task<SessionBillQuote> FreezeAsync(Guid session, FreezeBillRequest body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/internal/session-billing/sessions/{session}/freeze") { Content = JsonContent.Create(body) };
        request.Headers.Add(BillingMachineAuth.Header, BillingMachineAuth.Key(config));
        using var response = await http.SendAsync(request, ct);
        return await Read(response, ct);
    }
    public async Task<bool> PaidAsync(BillPaidNotice notice, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/session-billing/paid") { Content = JsonContent.Create(notice) };
        request.Headers.Add(BillingMachineAuth.Header, BillingMachineAuth.Key(config));
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return false;
        var ack = await response.Content.ReadFromJsonAsync<PaidAck>(cancellationToken: ct);
        return ack is not null && ack.Applied && ack.BillId == notice.BillId && ack.PaymentId == notice.PaymentId;
    }
    private sealed record PaidAck(Guid BillId, Guid PaymentId, bool Applied);
    private static async Task<SessionBillQuote> Read(HttpResponseMessage response, CancellationToken ct)
    {
        if ((int)response.StatusCode == 409)
        {
            try
            {
                var problem = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
                if (problem.ValueKind == JsonValueKind.Object && problem.TryGetProperty("errorCode", out var code)
                    && code.ValueKind == JsonValueKind.String)
                {
                    // Preserve known business reasons without forwarding private upstream details.
                    var conflict = code.GetString() switch
                    {
                        "billing.legacy_payment" => new ConflictException("Existing order payments require reconciliation before collecting a session bill.", "billing.legacy_payment"),
                        "billing.empty" => new ConflictException("There are no billable orders.", "billing.empty"),
                        "qr.session_closed" => new ConflictException("The table session is closed.", "qr.session_closed"),
                        "discount.review_required" => new ConflictException("Review and save the discount before collecting.", "discount.review_required"),
                        "order.cancel_pending" => new ConflictException("Wait for the order cancellation result before collecting.", "order.cancel_pending"),
                        _ => null
                    };
                    if (conflict is not null) throw conflict;
                }
            }
            catch (JsonException) { }
            throw new ConflictException("The session or bill changed. Reload before collecting.", "billing.stale");
        }
        if ((int)response.StatusCode is 403 or 404) throw new NotFoundException("Session", "requested");
        if (!response.IsSuccessStatusCode) throw new DependencyUnavailableException("ordering-billing");
        return await response.Content.ReadFromJsonAsync<SessionBillQuote>(cancellationToken: ct) ?? throw new DependencyUnavailableException("ordering-billing");
    }
}
