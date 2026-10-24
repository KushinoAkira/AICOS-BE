using System.Net;
using System.Net.Http.Json;
using AICOS.BuildingBlocks.Exceptions;

namespace AICOS.Ordering.Api.Integrations;

public interface ITableSessionClient { Task<TableSessionContract> GetAsync(Guid sessionId, CancellationToken cancellationToken); }
public sealed class TableSessionClient(HttpClient httpClient) : ITableSessionClient
{
    public async Task<TableSessionContract> GetAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var response = await httpClient.GetAsync($"/api/v1/tables/sessions/{sessionId}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new BusinessRuleException("order.invalid_session", "The table session is not available.");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TableSessionContract>(cancellationToken))!;
    }
}

public interface IMenuCatalogClient { Task<CatalogContract> GetAsync(CancellationToken cancellationToken); }
public sealed class MenuCatalogClient(HttpClient httpClient) : IMenuCatalogClient
{
    public async Task<CatalogContract> GetAsync(CancellationToken cancellationToken) =>
        (await httpClient.GetFromJsonAsync<CatalogContract>("/api/v1/menu/catalog", cancellationToken))!;
}

public sealed record TableSessionContract(Guid Id, Guid BranchId, string Status, IReadOnlyList<Guid> TableIds);
public sealed record CatalogContract(Guid BranchId, IReadOnlyList<CategoryContract> Categories);
public sealed record CategoryContract(Guid Id, IReadOnlyList<ProductContract> Products);
public sealed record ProductContract(Guid Id, string Name, IReadOnlyList<VariantContract> Variants, IReadOnlyList<OptionGroupContract> OptionGroups);
public sealed record VariantContract(Guid Id, string SizeName, decimal Price);
public sealed record OptionGroupContract(Guid Id, int MinSelect, int MaxSelect, IReadOnlyList<OptionContract> Options);
public sealed record OptionContract(Guid Id, string Name, decimal ExtraPrice);
