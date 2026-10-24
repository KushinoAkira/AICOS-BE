using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Exceptions;

namespace AICOS.WorkforceScheduling.Api.Infrastructure;

/// <summary>An order that was cancelled after it was paid (FR-30), as the Order service reports it.</summary>
public sealed record OrderException(
    Guid OrderId, string OrderCode, decimal FinalAmount, decimal RefundedAmount, string Status, string RefundStatus, string? PaymentProvider, DateTime? CancelledAtUtc)
{
    /// <summary>The money has not gone back yet: the exception is still open.</summary>
    public bool IsUnresolved => RefundStatus is "Needed" or "RefundPending" or "RefundFailed";
}

/// <summary>
/// Asks the Order service for the order exceptions of a branch, with the token of the manager (the Order service applies its own rules).
/// A cash closing shows the ones that happened during the shift and is not confirmed while some are still open.
/// </summary>
public interface IOrderExceptionDirectory
{
    Task<IReadOnlyList<OrderException>> ListAsync(Guid branchId, DateOnly from, DateOnly to, CancellationToken ct);
}

internal sealed class HttpOrderExceptionDirectory(HttpClient http) : IOrderExceptionDirectory
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<OrderException>> ListAsync(Guid branchId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync($"/api/v1/orders/exceptions?branchId={branchId}&from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}", ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new DependencyUnavailableException("order");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new DependencyUnavailableException("order");
            }

            var items = await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
            return items.EnumerateArray().Select(o => new OrderException(
                o.GetProperty("id").GetGuid(),
                o.GetProperty("orderCode").GetString() ?? string.Empty,
                o.GetProperty("finalAmount").GetDecimal(),
                o.GetProperty("refundedAmount").GetDecimal(),
                o.GetProperty("status").GetString() ?? string.Empty,
                o.GetProperty("refundStatus").GetString() ?? string.Empty,
                o.TryGetProperty("paymentProvider", out var p) ? p.GetString() : null,
                o.TryGetProperty("cancelledAtUtc", out var c) && c.ValueKind == JsonValueKind.String ? c.GetDateTime() : null)).ToList();
        }
    }
}
