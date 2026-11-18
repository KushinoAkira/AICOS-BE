using System.Net.Http.Json;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.TableReservation.Api.Domain;
namespace AICOS.TableReservation.Api.Features.DiningTables;
public interface ITableOccupancyClient { Task<IReadOnlySet<Guid>> ActiveAsync(Guid tenant, CancellationToken ct); }
internal sealed class HttpTableOccupancyClient(HttpClient http, IConfiguration config) : ITableOccupancyClient
{
    public async Task<IReadOnlySet<Guid>> ActiveAsync(Guid tenant, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/internal/session-billing/occupancy?tenantId={tenant}");
        request.Headers.Add(BillingMachineAuth.Header, BillingMachineAuth.Key(config));
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new DependencyUnavailableException("ordering-occupancy");
        var ids = await response.Content.ReadFromJsonAsync<List<Guid>>(cancellationToken: ct) ?? throw new DependencyUnavailableException("ordering-occupancy");
        return ids.ToHashSet();
    }
}
/// <summary>Session occupancy is owned by Ordering. Availability overrides are owned by Table.</summary>
public sealed class TableOccupancyProjection(ITableOccupancyClient sessions, IConfiguration config)
{
    public async Task<List<DiningTableResponse>> ApplyAsync(Guid tenant, IEnumerable<DiningTableResponse> tables, CancellationToken ct)
    {
        var rows = tables.ToList();
        if (!BillingMachineAuth.Enabled(config)) return rows;
        var active = await sessions.ActiveAsync(tenant, ct);
        return rows.Select(t => t.Status == TableStatus.OutOfService ? t : t with { Status = active.Contains(t.Id) ? TableStatus.Occupied : TableStatus.Available }).ToList();
    }
}
