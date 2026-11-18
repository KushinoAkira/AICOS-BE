using System.Net.Http.Json;
namespace AICOS.KitchenOperations.Api.Features.Barista;

public interface IBarOrderDirectory
{
    Task<DateTime?> CreatedAtAsync(Guid order, Guid branch, CancellationToken ct);
}
public sealed class BarOrderDirectory(HttpClient http) : IBarOrderDirectory
{
    private sealed record Source(Guid Id, Guid BranchId, DateTime CreatedAtUtc);
    public async Task<DateTime?> CreatedAtAsync(Guid order, Guid branch, CancellationToken ct)
    {
        try
        {
            var source = await http.GetFromJsonAsync<Source>($"/api/v1/orders/{order}", ct);
            return source?.Id == order && source.BranchId == branch && source.CreatedAtUtc.Kind == DateTimeKind.Utc ? source.CreatedAtUtc : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { return null; }
    }
}
