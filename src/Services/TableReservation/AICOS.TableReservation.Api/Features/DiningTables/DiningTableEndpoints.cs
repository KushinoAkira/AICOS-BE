using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.BuildingBlocks.Validation;
using AICOS.TableReservation.Api.Domain;
using AICOS.TableReservation.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.TableReservation.Api.Features.DiningTables;

public sealed record DiningTableRequest(Guid? BranchId, string? TableCode, int? Capacity, string? Area, int? PositionX, int? PositionY);

public sealed record ChangeTableStatusRequest(TableStatus? Status);

public sealed record DiningTableResponse(
    Guid Id, Guid BranchId, string TableCode, int Capacity, string? Area, int? PositionX, int? PositionY, TableStatus Status,
    string QrToken, string QrCodeUrl);

public sealed record QrLookupResponse(Guid BranchId, Guid TableId, string TableCode, bool CanOrder);

/// <summary>
/// UC-38 Manage Tables and Floor Plan (+ QR codes, FR-59). Routes: /api/v1/tables/dining-tables and /api/v1/tables/public/qr/{token}.
/// Read + status: all staff. Create/edit/delete/QR regenerate: SystemAdmin/Manager.
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
        group.MapDelete("/{id:guid}", DeleteAsync).RequireAuthorization(AicosPolicies.Management);

        api.MapGet("/public/qr/{token}", LookupQrAsync).WithTags("Public").AllowAnonymous();
    }

    private static async Task<List<DiningTableResponse>> ListAsync(
        TableReservationDbContext db, ICurrentUser user, IConfiguration config, Guid? branchId, string? area, TableStatus? status, CancellationToken ct)
    {
        var branch = BranchAccess.ResolveFilter(user, branchId);
        var query = db.DiningTables.AsNoTracking();
        if (branch is not null)
        {
            query = query.Where(t => t.BranchId == branch);
        }

        if (!string.IsNullOrWhiteSpace(area))
        {
            query = query.Where(t => t.Area == area.Trim());
        }

        if (status is not null)
        {
            query = query.Where(t => t.Status == status);
        }

        var tables = await query.OrderBy(t => t.Area).ThenBy(t => t.TableCode).ToListAsync(ct);
        return tables.Select(t => Map(t, config)).ToList();
    }

    private static async Task<DiningTableResponse> GetAsync(Guid id, TableReservationDbContext db, ICurrentUser user, IConfiguration config, CancellationToken ct) =>
        Map(await FindAsync(db, user, id, ct), config);

    private static async Task<IResult> CreateAsync(
        DiningTableRequest r, TableReservationDbContext db, ICurrentUser user, IConfiguration config, CancellationToken ct)
    {
        Validate(r);
        var branchId = BranchAccess.Resolve(user, r.BranchId);
        await EnsureCodeUniqueAsync(db, branchId, r.TableCode!, null, ct);

        var table = new DiningTable(branchId, r.TableCode!, r.Capacity!.Value, r.Area, r.PositionX, r.PositionY);
        db.DiningTables.Add(table);
        await db.SaveChangesAsync(ct);
        return Results.CreatedAtRoute("GetDiningTable", new { id = table.Id }, Map(table, config));
    }

    private static async Task<DiningTableResponse> UpdateAsync(
        Guid id, DiningTableRequest r, TableReservationDbContext db, ICurrentUser user, IConfiguration config, TimeProvider clock, CancellationToken ct)
    {
        Validate(r);
        var table = await FindAsync(db, user, id, ct);
        await EnsureCodeUniqueAsync(db, table.BranchId, r.TableCode!, id, ct);

        if (r.Capacity < table.Capacity)
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var tooBig = await db.Reservations.AnyAsync(x => x.TableId == id && Reservation.ActiveStatuses.Contains(x.Status)
                                                             && x.EndsAtUtc > now && x.PartySize > r.Capacity, ct);
            if (tooBig)
            {
                throw new ConflictException("Upcoming reservations on this table have more guests than the new capacity.", "table.capacity_in_use");
            }
        }

        table.Update(r.TableCode!, r.Capacity!.Value, r.Area, r.PositionX, r.PositionY);
        await db.SaveChangesAsync(ct);
        return Map(table, config);
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
        return Map(table, config);
    }

    /// <summary>FR-59: issue a new QR token, e.g. when a printed code was copied or damaged. Old codes stop working.</summary>
    private static async Task<DiningTableResponse> RegenerateQrAsync(
        Guid id, TableReservationDbContext db, ICurrentUser user, IConfiguration config, CancellationToken ct)
    {
        var table = await FindAsync(db, user, id, ct);
        table.RegenerateQr();
        await db.SaveChangesAsync(ct);
        return Map(table, config);
    }

    /// <summary>BR-30 (adapted): a table that is occupied or still has upcoming reservations cannot be deleted.</summary>
    private static async Task<IResult> DeleteAsync(Guid id, TableReservationDbContext db, ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        var table = await FindAsync(db, user, id, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        if (table.Status == TableStatus.Occupied
            || await db.Reservations.AnyAsync(x => x.TableId == id && Reservation.ActiveStatuses.Contains(x.Status) && x.EndsAtUtc > now, ct))
        {
            throw new ConflictException("The table is occupied or has upcoming reservations. Mark it OutOfService instead.", "table.in_use");
        }

        if (await db.Reservations.AnyAsync(x => x.TableId == id, ct))
        {
            throw new ConflictException("The table has reservation history. Mark it OutOfService instead of deleting it.", "table.has_history");
        }

        db.DiningTables.Remove(table);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>Public: resolves the token in a scanned QR code to the branch/table for QR ordering (NFR-16).</summary>
    private static async Task<QrLookupResponse> LookupQrAsync(string token, TableReservationDbContext db, CancellationToken ct)
    {
        var table = await db.DiningTables.AsNoTracking().FirstOrDefaultAsync(t => t.QrToken == token.Trim().ToLower(), ct)
            ?? throw new NotFoundException("QR code", token);
        return new QrLookupResponse(table.BranchId, table.Id, table.TableCode, table.Status != TableStatus.OutOfService);
    }

    private static void Validate(DiningTableRequest r)
    {
        var errors = new ValidationErrors();
        CommonRules.Required(errors, "TableCode", r.TableCode, "Table code", DiningTable.CodeMaxLength);
        if (!errors.Has("TableCode") && !r.TableCode!.Trim().All(c => char.IsLetterOrDigit(c) || c is '-' or '_'))
        {
            errors.Add("TableCode", "Table code may only contain letters, digits, '-' and '_'.");
        }

        CommonRules.Range(errors, "Capacity", r.Capacity, "Capacity", 1, 50);
        CommonRules.MaxLength(errors, "Area", r.Area, "Area", DiningTable.AreaMaxLength);
        if (r.PositionX is < 0 or > 10000 || r.PositionY is < 0 or > 10000)
        {
            errors.Add("PositionX", "Floor-plan positions must be between 0 and 10000.");
        }

        errors.ThrowIfAny();
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
        var table = await db.DiningTables.FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException("Table", id);
        BranchAccess.EnsureCanAccess(user, table.BranchId);
        return table;
    }

    internal static DiningTableResponse Map(DiningTable t, IConfiguration config)
    {
        var baseUrl = (config["Tables:QrBaseUrl"] ?? "http://localhost:3000/qr").TrimEnd('/');
        return new DiningTableResponse(t.Id, t.BranchId, t.TableCode, t.Capacity, t.Area, t.PositionX, t.PositionY, t.Status, t.QrToken, $"{baseUrl}/{t.QrToken}");
    }
}
