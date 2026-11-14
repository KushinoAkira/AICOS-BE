using System.Net.Http.Json;
namespace AICOS.KitchenOperations.Api.Features.Barista;
public interface IBarLocationDirectory
{
    Task<string?> AreaAsync(Guid branch, string? table, CancellationToken ct);
}
public sealed class BarLocationDirectory(HttpClient http) : IBarLocationDirectory
{
    private sealed record Table(Guid BranchId, string TableCode, string AreaName);
    public async Task<string?> AreaAsync(Guid branch, string? table, CancellationToken ct)
    {
        if (table is null)
            return null;
        try
        {
            var rows = await http.GetFromJsonAsync<List<Table>>($"/api/v1/tables/dining-tables?branchId={branch}", ct);
            return rows?.SingleOrDefault(t => t.BranchId == branch && t.TableCode == table)?.AreaName;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { return null; }
    }
}
