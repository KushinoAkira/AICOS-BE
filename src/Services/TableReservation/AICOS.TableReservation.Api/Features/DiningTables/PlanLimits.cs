using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.TableReservation.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.TableReservation.Api.Features.DiningTables;

/// <summary>What the plan of the caller's brand allows for tables. A null limit means unlimited.</summary>
public sealed record TablePlanLimits(bool HasPlan, int? MaxTablesPerBranch);

/// <summary>Asks the Identity service, which owns the plans, about the plan of the caller's brand.</summary>
public interface IPlanLimitsClient
{
    Task<TablePlanLimits> GetAsync(CancellationToken ct);
}

internal sealed class HttpPlanLimitsClient(HttpClient http) : IPlanLimitsClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<TablePlanLimits> GetAsync(CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync("/api/v1/identity/subscription/limits", ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new DependencyUnavailableException("identity");
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            {
                throw new ForbiddenException("You cannot read the plan of this brand.", "subscription.forbidden");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new DependencyUnavailableException("identity");
            }

            return await response.Content.ReadFromJsonAsync<TablePlanLimits>(Json, ct) ?? throw new DependencyUnavailableException("identity");
        }
    }
}

/// <summary>BR-11 for tables: the limit counts per branch and only stops new tables. A brand that moved to a smaller plan keeps the tables it has.</summary>
internal static class TablePlanRules
{
    public static async Task EnsureRoomAsync(TableReservationDbContext db, IPlanLimitsClient limits, Guid branchId, int adding, CancellationToken ct)
    {
        var plan = await limits.GetAsync(ct);
        if (plan.MaxTablesPerBranch is not { } max)
        {
            return;
        }

        var used = await db.DiningTables.CountAsync(t => t.BranchId == branchId, ct);
        if (used + adding > max)
        {
            throw new BusinessRuleException(
                "subscription.table_limit_reached",
                $"The plan allows at most {max} tables per branch. This branch has {used}, so {Math.Max(0, max - used)} more can be added.");
        }
    }
}
