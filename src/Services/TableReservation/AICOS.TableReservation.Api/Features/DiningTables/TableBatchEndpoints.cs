using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.BuildingBlocks.Validation;
using AICOS.TableReservation.Api.Domain;
using AICOS.TableReservation.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.TableReservation.Api.Features.DiningTables;

/// <summary>One table to create. The area is named by id (generate) or by name (CSV import, where a new name creates the area).</summary>
public sealed record TableBatchRow(Guid? AreaId, string? AreaName, string? TableCode, int? Capacity, int? PositionX = null, int? PositionY = null);

/// <summary><see cref="BranchId"/> is only read for a BrandAdmin; a BranchManager always works on their own branch.</summary>
public sealed record TableBatchRequest(Guid? BranchId, List<TableBatchRow>? Rows);

public sealed record TableBatchResponse(int Created, int AreasCreated, List<DiningTableResponse> Tables);

/// <summary>
/// UC-25, UC-26: setting up many tables at once. Routes: /api/v1/tables/dining-tables/batch. All or nothing: one bad row creates no table, and the
/// answer names every bad row (<c>errors["Rows[3]"]</c>). Each table gets its own QR token.
/// </summary>
public static class TableBatchEndpoints
{
    public const int MaxRows = 300;

    public static void MapTableBatchEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/dining-tables/batch", CreateBatchAsync).WithTags("Dining tables").RequireAuthorization(AicosPolicies.Management);
    }

    private static async Task<IResult> CreateBatchAsync(
        TableBatchRequest r, TableReservationDbContext db, ICurrentUser user, IBranchDirectory directory, IPlanLimitsClient limits, IConfiguration config,
        TimeProvider clock, CancellationToken ct)
    {
        var rows = r.Rows ?? [];
        if (rows.Count is 0 or > MaxRows)
        {
            throw new ValidationFailedException("Rows", $"Give 1-{MaxRows} tables.");
        }

        var tenantId = TenantScope.Require(user);
        var branchId = await BranchScope.ResolveAsync(user, directory, r.BranchId, requireActive: true, ct);
        var areas = await db.TableAreas.Where(a => a.TenantId == tenantId && a.BranchId == branchId).ToListAsync(ct);
        var taken = (await db.DiningTables.AsNoTracking().Where(t => t.BranchId == branchId).Select(t => t.TableCode).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var errors = new ValidationErrors();
        var newAreas = new List<TableArea>();
        var nextSort = areas.Count == 0 ? 0 : areas.Max(a => a.SortOrder) + 1;
        var tables = new List<DiningTable>();
        var now = clock.GetUtcNow().UtcDateTime;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var problem = Check(row, taken);
            var area = problem is null ? FindArea(row, areas, newAreas) : null;
            if (problem is null && area is null)
            {
                if (row.AreaId is not null)
                {
                    problem = "the area does not exist in this branch";
                }
                else if (string.IsNullOrWhiteSpace(row.AreaName) || row.AreaName.Trim().Length > TableArea.NameMaxLength)
                {
                    problem = $"the area name is required, at most {TableArea.NameMaxLength} characters";
                }
                else
                {
                    area = new TableArea(tenantId, branchId, row.AreaName, nextSort++);
                    newAreas.Add(area);
                }
            }

            if (problem is not null)
            {
                errors.Add($"Rows[{i}]", $"Row {i + 1}: {problem}.");
                continue;
            }

            taken.Add(row.TableCode!.Trim());
            tables.Add(new DiningTable(tenantId, branchId, area!.Id, row.TableCode!, row.Capacity!.Value, row.PositionX, row.PositionY, now));
        }

        errors.ThrowIfAny();
        await TablePlanRules.EnsureRoomAsync(db, limits, branchId, tables.Count, ct);

        db.TableAreas.AddRange(newAreas);
        await db.SaveChangesAsync(ct);   // the areas first: a table refers to its area
        db.DiningTables.AddRange(tables);
        await db.SaveChangesAsync(ct);

        var names = areas.Concat(newAreas).ToDictionary(a => a.Id, a => a.Name);
        return Results.Created(
            "/api/v1/tables/dining-tables", new TableBatchResponse(tables.Count, newAreas.Count, tables.Select(t => DiningTableEndpoints.Map(t, names, config)).ToList()));
    }

    private static string? Check(TableBatchRow row, HashSet<string> taken)
    {
        var code = row.TableCode?.Trim();
        if (string.IsNullOrEmpty(code) || code.Length > DiningTable.CodeMaxLength)
        {
            return $"the table code is required, at most {DiningTable.CodeMaxLength} characters";
        }

        if (!DiningTableEndpoints.IsValidCode(code))
        {
            return $"table code '{code}' may only contain letters, digits, '-' and '_'";
        }

        if (taken.Contains(code))
        {
            return $"table '{code.ToUpperInvariant()}' already exists";
        }

        if (row.Capacity is null or < 1 or > 50)
        {
            return "capacity must be between 1 and 50";
        }

        return row.PositionX is < 0 or > 10000 || row.PositionY is < 0 or > 10000 ? "floor-plan positions must be between 0 and 10000" : null;
    }

    private static TableArea? FindArea(TableBatchRow row, List<TableArea> areas, List<TableArea> newAreas)
    {
        if (row.AreaId is { } id)
        {
            return areas.Find(a => a.Id == id);
        }

        var name = row.AreaName?.Trim();
        return areas.Concat(newAreas).FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
    }
}
