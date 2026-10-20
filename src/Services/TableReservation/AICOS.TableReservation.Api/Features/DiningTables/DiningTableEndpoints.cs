using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.BuildingBlocks.Validation;
using AICOS.TableReservation.Api.Domain;
using AICOS.TableReservation.Api.Features.Areas;
using AICOS.TableReservation.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using QRCoder;

namespace AICOS.TableReservation.Api.Features.DiningTables;

public sealed record DiningTableRequest(Guid? BranchId, Guid? AreaId, string? TableCode, int? Capacity, int? PositionX, int? PositionY);

public sealed record ChangeTableStatusRequest(TableStatus? Status);

public sealed record DiningTableResponse(
    Guid Id,
    Guid BranchId,
    Guid AreaId,
    string AreaName,
    string TableCode,
    int Capacity,
    int? PositionX,
    int? PositionY,
    TableStatus Status,
    string QrToken,
    string QrCodeUrl,
    DateTime QrIssuedAtUtc);

public sealed record FloorPlanArea(Guid Id, string Name, int SortOrder, List<DiningTableResponse> Tables);

/// <summary>The floor plan of a branch: its areas, each with the tables drawn in it (UC-25).</summary>
public sealed record FloorPlanResponse(Guid BranchId, List<FloorPlanArea> Areas);

/// <summary>What the QR ordering page learns from a scanned code: which brand, branch and table it is, and whether ordering is open.</summary>
public sealed record QrLookupResponse(Guid TenantId, Guid BranchId, Guid TableId, string TableCode, string AreaName, bool CanOrder);

/// <summary>
/// UC-25 Manage Floor Plan and Tables, UC-26 Generate and Manage Table QR Codes. Routes: /api/v1/tables/dining-tables,
/// /api/v1/tables/floor-plan and /api/v1/tables/public/qr/{token}. Read and status: staff of the branch.
/// Create, edit, delete, QR: BranchManager of the branch, or the BrandAdmin for any branch of the brand.
/// </summary>
public static class DiningTableEndpoints
{
    public static void MapDiningTableEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/dining-tables").WithTags("Dining tables");

        group.MapGet("/", ListAsync).RequireAuthorization(AicosPolicies.Staff);
        group.MapGet("/{id:guid}", GetAsync).RequireAuthorization(AicosPolicies.Staff).WithName("GetDiningTable");
        group.MapPost("/", CreateAsync).RequireAuthorization(AicosPolicies.Management);
        group.MapPut("/{id:guid}", UpdateAsync).RequireAuthorization(AicosPolicies.Management);
        group.MapPatch("/{id:guid}/status", ChangeStatusAsync).RequireAuthorization(AicosPolicies.Staff);
        group.MapPost("/{id:guid}/qr/regenerate", RegenerateQrAsync).RequireAuthorization(AicosPolicies.Management);
        group.MapGet("/{id:guid}/qr.svg", QrImageAsync).RequireAuthorization(AicosPolicies.Management);
        group.MapDelete("/{id:guid}", DeleteAsync).RequireAuthorization(AicosPolicies.Management);

        api.MapGet("/floor-plan", FloorPlanAsync).WithTags("Dining tables").RequireAuthorization(AicosPolicies.Staff);
        api.MapGet("/public/qr/{token}", LookupQrAsync).WithTags("Public").AllowAnonymous();
    }

    private static async Task<List<DiningTableResponse>> ListAsync(
        TableReservationDbContext db, ICurrentUser user, IBranchDirectory directory, IConfiguration config,
        Guid? branchId, Guid? areaId, TableStatus? status, CancellationToken ct)
    {
        var tenantId = TenantScope.Require(user);
        var branch = await Scope.FilterAsync(user, directory, branchId, ct);
        var query = db.DiningTables.AsNoTracking().Where(t => t.TenantId == tenantId);
        if (branch is not null)
        {
            query = query.Where(t => t.BranchId == branch);
        }

        if (areaId is not null)
        {
            query = query.Where(t => t.AreaId == areaId);
        }

        if (status is not null)
        {
            query = query.Where(t => t.Status == status);
        }

        var tables = await query.OrderBy(t => t.BranchId).ThenBy(t => t.TableCode).ToListAsync(ct);
        var areas = await AreaNamesAsync(db, tables.Select(t => t.AreaId), ct);
        return tables.Select(t => Map(t, areas, config)).ToList();
    }

    private static async Task<FloorPlanResponse> FloorPlanAsync(
        TableReservationDbContext db, ICurrentUser user, IBranchDirectory directory, IConfiguration config, Guid? branchId, CancellationToken ct)
    {
        var tenantId = TenantScope.Require(user);
        var branch = await BranchScope.ResolveAsync(user, directory, branchId, requireActive: false, ct);

        var areas = await db.TableAreas.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.BranchId == branch)
            .OrderBy(a => a.SortOrder).ThenBy(a => a.Name)
            .ToListAsync(ct);
        var tables = await db.DiningTables.AsNoTracking().Where(t => t.TenantId == tenantId && t.BranchId == branch).OrderBy(t => t.TableCode).ToListAsync(ct);
        var names = areas.ToDictionary(a => a.Id, a => a.Name);

        return new FloorPlanResponse(branch, areas
            .Select(a => new FloorPlanArea(a.Id, a.Name, a.SortOrder, tables.Where(t => t.AreaId == a.Id).Select(t => Map(t, names, config)).ToList()))
            .ToList());
    }

    private static async Task<DiningTableResponse> GetAsync(
        Guid id, TableReservationDbContext db, ICurrentUser user, IConfiguration config, CancellationToken ct)
    {
        var table = await FindAsync(db, user, id, ct);
        return Map(table, await AreaNamesAsync(db, [table.AreaId], ct), config);
    }

    private static async Task<IResult> CreateAsync(
        DiningTableRequest r, TableReservationDbContext db, ICurrentUser user, IBranchDirectory directory, IConfiguration config,
        TimeProvider clock, CancellationToken ct)
    {
        Validate(r);
        var tenantId = TenantScope.Require(user);
        var branchId = await BranchScope.ResolveAsync(user, directory, r.BranchId, requireActive: true, ct);
        await EnsureAreaInBranchAsync(db, r.AreaId!.Value, branchId, tenantId, ct);
        await EnsureCodeUniqueAsync(db, branchId, r.TableCode!, null, ct);

        var table = new DiningTable(tenantId, branchId, r.AreaId!.Value, r.TableCode!, r.Capacity!.Value, r.PositionX, r.PositionY, clock.GetUtcNow().UtcDateTime);
        db.DiningTables.Add(table);
        await db.SaveChangesAsync(ct);
        return Results.CreatedAtRoute("GetDiningTable", new { id = table.Id }, Map(table, await AreaNamesAsync(db, [table.AreaId], ct), config));
    }

    private static async Task<DiningTableResponse> UpdateAsync(
        Guid id, DiningTableRequest r, TableReservationDbContext db, ICurrentUser user, IConfiguration config, CancellationToken ct)
    {
        Validate(r);
        var table = await FindAsync(db, user, id, ct);
        await EnsureAreaInBranchAsync(db, r.AreaId!.Value, table.BranchId, table.TenantId, ct);
        await EnsureCodeUniqueAsync(db, table.BranchId, r.TableCode!, id, ct);

        table.Update(r.AreaId!.Value, r.TableCode!, r.Capacity!.Value, r.PositionX, r.PositionY);
        await db.SaveChangesAsync(ct);
        return Map(table, await AreaNamesAsync(db, [table.AreaId], ct), config);
    }

    private static async Task<DiningTableResponse> ChangeStatusAsync(
        Guid id, ChangeTableStatusRequest r, TableReservationDbContext db, ICurrentUser user, IConfiguration config, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        CommonRules.RequiredEnum(errors, "Status", r.Status, "Status");
        errors.ThrowIfAny();

        var table = await FindAsync(db, user, id, ct);
        table.ChangeStatus(r.Status!.Value);
        await db.SaveChangesAsync(ct);
        return Map(table, await AreaNamesAsync(db, [table.AreaId], ct), config);
    }

    /// <summary>BR-18: issue a new QR token, e.g. when a printed code was copied or damaged. The old code stops working at once.</summary>
    private static async Task<DiningTableResponse> RegenerateQrAsync(
        Guid id, TableReservationDbContext db, ICurrentUser user, IConfiguration config, TimeProvider clock, CancellationToken ct)
    {
        var table = await FindAsync(db, user, id, ct);
        table.RegenerateQr(clock.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct);
        return Map(table, await AreaNamesAsync(db, [table.AreaId], ct), config);
    }

    /// <summary>The QR code to print, as an SVG image. It encodes <c>Tables:QrBaseUrl</c> + the table token.</summary>
    private static async Task<IResult> QrImageAsync(Guid id, TableReservationDbContext db, ICurrentUser user, IConfiguration config, CancellationToken ct)
    {
        var table = await FindAsync(db, user, id, ct);
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(QrUrl(table, config), QRCodeGenerator.ECCLevel.M);
        var svg = new SvgQRCode(data).GetGraphic(8);
        return Results.Content(svg, "image/svg+xml");
    }

    /// <summary>A table that is occupied cannot be deleted; mark it OutOfService instead.</summary>
    private static async Task<IResult> DeleteAsync(Guid id, TableReservationDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var table = await FindAsync(db, user, id, ct);
        if (table.Status == TableStatus.Occupied)
        {
            throw new ConflictException("The table is occupied. Mark it OutOfService instead.", "table.in_use");
        }

        db.DiningTables.Remove(table);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>Public: resolves the token in a scanned QR code to the brand, branch and table for QR ordering (UC-49).</summary>
    private static async Task<QrLookupResponse> LookupQrAsync(string token, TableReservationDbContext db, CancellationToken ct)
    {
        var normalized = token.Trim().ToLowerInvariant();
        var row = await (from t in db.DiningTables.AsNoTracking()
                         join a in db.TableAreas.AsNoTracking() on t.AreaId equals a.Id
                         where t.QrToken == normalized
                         select new { Table = t, AreaName = a.Name }).FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("QR code", token);

        return new QrLookupResponse(
            row.Table.TenantId, row.Table.BranchId, row.Table.Id, row.Table.TableCode, row.AreaName, row.Table.Status != TableStatus.OutOfService);
    }

    private static void Validate(DiningTableRequest r)
    {
        var errors = new ValidationErrors();
        CommonRules.RequiredId(errors, "AreaId", r.AreaId, "Area");
        CommonRules.Required(errors, "TableCode", r.TableCode, "Table code", DiningTable.CodeMaxLength);
        if (!errors.Has("TableCode") && !r.TableCode!.Trim().All(c => char.IsLetterOrDigit(c) || c is '-' or '_'))
        {
            errors.Add("TableCode", "Table code may only contain letters, digits, '-' and '_'.");
        }

        CommonRules.Range(errors, "Capacity", r.Capacity, "Capacity", 1, 50);
        if (r.PositionX is < 0 or > 10000 || r.PositionY is < 0 or > 10000)
        {
            errors.Add("PositionX", "Floor-plan positions must be between 0 and 10000.");
        }

        errors.ThrowIfAny();
    }

    private static async Task EnsureAreaInBranchAsync(TableReservationDbContext db, Guid areaId, Guid branchId, Guid tenantId, CancellationToken ct)
    {
        if (!await db.TableAreas.AnyAsync(a => a.Id == areaId && a.BranchId == branchId && a.TenantId == tenantId, ct))
        {
            throw new ValidationFailedException("AreaId", "Area does not exist in this branch.");
        }
    }

    private static async Task EnsureCodeUniqueAsync(TableReservationDbContext db, Guid branchId, string code, Guid? excludeId, CancellationToken ct)
    {
        var normalized = code.Trim().ToUpperInvariant();
        if (await db.DiningTables.AnyAsync(t => t.BranchId == branchId && t.TableCode == normalized && t.Id != excludeId, ct))
        {
            throw new ConflictException($"Table '{normalized}' already exists in this branch.", "table.duplicate_code");
        }
    }

    internal static async Task<DiningTable> FindAsync(TableReservationDbContext db, ICurrentUser user, Guid id, CancellationToken ct)
    {
        var tenantId = TenantScope.Require(user);
        var table = await db.DiningTables.FirstOrDefaultAsync(t => t.Id == id && t.TenantId == tenantId, ct) ?? throw new NotFoundException("Table", id);
        BranchAccess.EnsureCanAccess(user, table.BranchId);
        return table;
    }

    private static async Task<Dictionary<Guid, string>> AreaNamesAsync(TableReservationDbContext db, IEnumerable<Guid> areaIds, CancellationToken ct)
    {
        var ids = areaIds.Distinct().ToList();
        return await db.TableAreas.AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name, ct);
    }

    private static string QrUrl(DiningTable t, IConfiguration config) =>
        $"{(config["Tables:QrBaseUrl"] ?? "http://localhost:3000/qr").TrimEnd('/')}/{t.QrToken}";

    private static DiningTableResponse Map(DiningTable t, Dictionary<Guid, string> areaNames, IConfiguration config) => new(
        t.Id, t.BranchId, t.AreaId, areaNames.GetValueOrDefault(t.AreaId, string.Empty), t.TableCode, t.Capacity, t.PositionX, t.PositionY,
        t.Status, t.QrToken, QrUrl(t, config), t.QrIssuedAtUtc);
}
