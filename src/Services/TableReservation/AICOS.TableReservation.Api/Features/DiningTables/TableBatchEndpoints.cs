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

public sealed record TablePosition(Guid? TableId, int? PositionX, int? PositionY);

/// <summary>Tables that moved on the floor plan. <see cref="BranchId"/> is only read for a BrandAdmin.</summary>
public sealed record TableLayoutRequest(Guid? BranchId, List<TablePosition>? Positions);

public sealed record TableLayoutResponse(int Moved);

/// <summary>Copies the floor plan of one branch to others of the brand.</summary>
public sealed record FloorPlanCloneRequest(Guid? FromBranchId, List<Guid>? ToBranchIds);

public sealed record ClonedBranch(Guid BranchId, int Tables, int AreasCreated);

public sealed record FloorPlanCloneResponse(List<ClonedBranch> Branches);

/// <summary>
/// UC-25, UC-26: setting up many tables at once. Routes: /api/v1/tables/dining-tables/batch. All or nothing: one bad row creates no table, and the
/// answer names every bad row (<c>errors["Rows[3]"]</c>). Each table gets its own QR token.
/// </summary>
public static class TableBatchEndpoints
{
    public const int MaxRows = 300;
    private const int MaxCloneTargets = 50;

    public static void MapTableBatchEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/dining-tables/batch", CreateBatchAsync).WithTags("Dining tables").RequireAuthorization(AicosPolicies.Management);
        api.MapPut("/floor-plan/layout", SaveLayoutAsync).WithTags("Dining tables").RequireAuthorization(AicosPolicies.Management);
        api.MapPost("/floor-plan/clone", CloneAsync).WithTags("Dining tables").RequireAuthorization(AicosPolicies.AdminOnly);
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

    /// <summary>UC-25: saves where tables stand, all together. Two tables of one area cannot share a cell once the move is done.</summary>
    private static async Task<TableLayoutResponse> SaveLayoutAsync(
        TableLayoutRequest r, TableReservationDbContext db, ICurrentUser user, IBranchDirectory directory, CancellationToken ct)
    {
        var positions = r.Positions ?? [];
        if (positions.Count is 0 or > MaxRows)
        {
            throw new ValidationFailedException("Positions", $"Give 1-{MaxRows} tables.");
        }

        var tenantId = TenantScope.Require(user);
        var branchId = await BranchScope.ResolveAsync(user, directory, r.BranchId, requireActive: true, ct);
        var tables = await db.DiningTables.Where(t => t.TenantId == tenantId && t.BranchId == branchId).ToListAsync(ct);

        var errors = new ValidationErrors();
        var moved = new List<DiningTable>();
        for (var i = 0; i < positions.Count; i++)
        {
            var p = positions[i];
            var table = tables.Find(t => t.Id == p.TableId);
            if (table is null)
            {
                errors.Add($"Positions[{i}]", "The table does not exist in this branch.");
            }
            else if (p.PositionX is null or < 0 or > 10000 || p.PositionY is null or < 0 or > 10000)
            {
                errors.Add($"Positions[{i}]", $"Table {table.TableCode}: floor-plan positions must be between 0 and 10000.");
            }
            else
            {
                table.MoveTo(p.PositionX.Value, p.PositionY.Value);
                moved.Add(table);
            }
        }

        foreach (var table in moved)
        {
            if (tables.Any(t => t.Id != table.Id && t.AreaId == table.AreaId && t.PositionX == table.PositionX && t.PositionY == table.PositionY))
            {
                errors.Add("Positions", $"Table {table.TableCode} stands on a cell another table already uses.");
            }
        }

        errors.ThrowIfAny();
        await db.SaveChangesAsync(ct);
        return new TableLayoutResponse(moved.Count);
    }

    /// <summary>
    /// Set up one shop, then copy it: areas, table codes, seats and positions go to each target with a new QR token per table, so a printed code
    /// belongs to one table only. A target that already has tables is refused, and one refused target stops the whole copy.
    /// </summary>
    private static async Task<IResult> CloneAsync(
        FloorPlanCloneRequest r, TableReservationDbContext db, ICurrentUser user, IBranchDirectory directory, IPlanLimitsClient limits, TimeProvider clock, CancellationToken ct)
    {
        var targets = (r.ToBranchIds ?? []).Distinct().ToList();
        var errors = new ValidationErrors();
        CommonRules.RequiredId(errors, "FromBranchId", r.FromBranchId, "Source branch");
        if (targets.Count is 0 or > MaxCloneTargets)
        {
            errors.Add("ToBranchIds", $"Choose 1-{MaxCloneTargets} branches.");
        }
        else if (targets.Contains(r.FromBranchId ?? Guid.Empty))
        {
            errors.Add("ToBranchIds", "A branch cannot be copied onto itself.");
        }

        errors.ThrowIfAny();

        var tenantId = TenantScope.Require(user);
        var source = r.FromBranchId!.Value;
        await BranchScope.EnsureUsableAsync(directory, source, requireActive: false, ct);
        var sourceAreas = await db.TableAreas.AsNoTracking().Where(a => a.TenantId == tenantId && a.BranchId == source).ToListAsync(ct);
        var sourceTables = await db.DiningTables.AsNoTracking().Where(t => t.TenantId == tenantId && t.BranchId == source).ToListAsync(ct);
        if (sourceTables.Count == 0)
        {
            throw new BusinessRuleException("floor_plan.source_empty", "The source branch has no tables to copy.");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var done = new List<ClonedBranch>();
        var tables = new List<DiningTable>();
        foreach (var target in targets)
        {
            await BranchScope.EnsureUsableAsync(directory, target, requireActive: true, ct);
            if (await db.DiningTables.AnyAsync(t => t.TenantId == tenantId && t.BranchId == target, ct))
            {
                throw new ConflictException("A target branch already has tables. Remove them first, or choose another branch.", "floor_plan.target_not_empty");
            }

            await TablePlanRules.EnsureRoomAsync(db, limits, target, sourceTables.Count, ct);

            var areas = await db.TableAreas.Where(a => a.TenantId == tenantId && a.BranchId == target).ToListAsync(ct);
            var created = 0;
            var areaIds = new Dictionary<Guid, Guid>();
            foreach (var area in sourceAreas)
            {
                var same = areas.Find(a => string.Equals(a.Name, area.Name, StringComparison.OrdinalIgnoreCase));
                if (same is null)
                {
                    same = new TableArea(tenantId, target, area.Name, area.SortOrder);
                    db.TableAreas.Add(same);
                    created++;
                }

                areaIds[area.Id] = same.Id;
            }

            tables.AddRange(sourceTables.Select(t => new DiningTable(tenantId, target, areaIds[t.AreaId], t.TableCode, t.Capacity, t.PositionX, t.PositionY, now)));
            done.Add(new ClonedBranch(target, sourceTables.Count, created));
        }

        await db.SaveChangesAsync(ct);   // the areas first: a table refers to its area
        db.DiningTables.AddRange(tables);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new FloorPlanCloneResponse(done));
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
