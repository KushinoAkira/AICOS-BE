using System.Net;
using System.Net.Http.Json;
using AICOS.BuildingBlocks.Exceptions;

namespace AICOS.Payments.Api.Integrations;

public interface IOrderBillingClient
{
    Task<BillingOrderContract> GetAsync(Guid orderId, CancellationToken cancellationToken);
    Task<IReadOnlyList<BillingOrderContract>> GetBySessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken);
}

public sealed class OrderBillingClient(HttpClient httpClient) : IOrderBillingClient
{
    public async Task<BillingOrderContract> GetAsync(
        Guid orderId,
        CancellationToken cancellationToken)
    {
        var response = await httpClient.GetAsync(
            $"/api/v1/orders/{orderId}",
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new BusinessRuleException(
                "billing.order_not_found",
                "The order is not available in this branch.");
        }

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<BillingOrderContract>(
            cancellationToken))!;
    }

    public async Task<IReadOnlyList<BillingOrderContract>> GetBySessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var response = await httpClient.GetFromJsonAsync<SessionOrdersContract>(
            $"/api/v1/orders/?sessionId={sessionId}",
            cancellationToken);
        return response?.Orders ?? [];
    }
}

public sealed record SessionOrdersContract(
    Guid SessionId,
    IReadOnlyList<BillingOrderContract> Orders);

public sealed record BillingOrderContract(
    Guid Id,
    string OrderType,
    Guid? SessionId,
    decimal TotalAmount,
    IReadOnlyList<BillingOrderItemContract> Items);

public sealed record BillingOrderItemContract(
    Guid Id,
    int Quantity,
    decimal UnitPrice,
    IReadOnlyList<BillingOrderOptionContract> Options);

public sealed record BillingOrderOptionContract(decimal ExtraPrice);
