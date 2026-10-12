using System.Net;
using System.Net.Http.Json;
using AICOS.BuildingBlocks.Exceptions;

namespace AICOS.InventoryPurchasing.Api.Infrastructure;

public sealed record MenuProduct(Guid Id, Guid BranchId, string Name, decimal BasePrice, bool IsAvailable);

/// <summary>Synchronous REST call to Menu Service (service-to-service, not through the gateway).</summary>
public interface IMenuClient
{
    /// <returns>The product, or null if Menu Service says it does not exist.</returns>
    Task<MenuProduct?> GetProductAsync(Guid productId, CancellationToken ct);
}

internal sealed class MenuClient(HttpClient http, ILogger<MenuClient> logger) : IMenuClient
{
    public async Task<MenuProduct?> GetProductAsync(Guid productId, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync($"/api/v1/menu/public/products/{productId}", ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<MenuProduct>(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or Polly.Timeout.TimeoutRejectedException
                                       or Polly.CircuitBreaker.BrokenCircuitException)
        {
            logger.LogWarning(ex, "Menu service call failed for product {ProductId}", productId);
            throw new DependencyUnavailableException("Menu");
        }
    }
}
