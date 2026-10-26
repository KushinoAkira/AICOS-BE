using System.Net.Http.Json;
using AICOS.BuildingBlocks.Exceptions;

namespace AICOS.TableReservation.Api.Integrations;

public interface IPaymentSettlementClient
{
    Task<PaymentSettlementContract> GetAsync(
        Guid sessionId,
        CancellationToken cancellationToken);
}

public sealed class PaymentSettlementClient(HttpClient httpClient) : IPaymentSettlementClient
{
    public async Task<PaymentSettlementContract> GetAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync(
                $"/api/v1/payments/sessions/{sessionId}/settlement-status",
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw SettlementUnavailable();
            }

            return await response.Content.ReadFromJsonAsync<PaymentSettlementContract>(
                cancellationToken)
                ?? throw SettlementUnavailable();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw SettlementUnavailable();
        }
        catch (HttpRequestException)
        {
            throw SettlementUnavailable();
        }
    }

    private static ConflictException SettlementUnavailable() =>
        new(
            "Payment settlement could not be verified. The table session remains open.",
            "table.settlement_unavailable");
}

public sealed record PaymentSettlementContract(
    Guid SessionId,
    Guid? BillId,
    string? BillStatus,
    decimal TotalAmount,
    decimal SucceededAmount,
    decimal OutstandingAmount,
    bool HasPendingPayment,
    bool Settled,
    long Version);
