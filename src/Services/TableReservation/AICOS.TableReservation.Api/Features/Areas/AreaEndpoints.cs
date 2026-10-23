using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.BuildingBlocks.Validation;
using AICOS.TableReservation.Api.Domain;
using AICOS.TableReservation.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.TableReservation.Api.Features.Areas;

/// <summary>POST /areas. <see cref="BranchId"/> is only read for a BrandAdmin; a BranchManager always works on their own branch.</summary>
public sealed record CreateAreaRequest(Guid? BranchId, string? Name, int? SortOrder);

public sealed record UpdateAreaRequest(string? Name, int? SortOrder);

public sealed record AreaResponse(Guid Id, Guid BranchId, string Name, int SortOrder, int TableCount)
{
    public static AreaResponse From(TableArea a, int tableCount) => new(a.Id, a.BranchId, a.Name, a.SortOrder, tableCount);
}

/// <summary>
/// UC-25 Manage Floor Plan - the areas (zones) of a branch. Routes: /api/v1/tables/areas.
/// Read: staff of the branch. Write: BranchManager of the branch, or the BrandAdmin for any branch of the brand.
/// </summary>
public static class AreaEndpoints
{
    public static void MapAreaEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/areas").WithTags("Areas");

        group.MapGet("/", ListAsync).RequireAuthorization(AicosPolicies.Staff);
        group.MapGet("/{id:guid}", GetAsync).RequireAuthorization(AicosPolicies.Staff).WithName("GetArea");
        group.MapPost("/", CreateAsync).RequireAuthorization(AicosPolicies.Management);
        group.MapPut("/{id:guid}", UpdateAsync).RequireAuthorization(AicosPolicies.Management);
        group.MapDelete("/{id:guid}", DeleteAsync).RequireAuthorization(AicosPolicies.Management);
    }

    private static async Task<List<AreaResponse>> ListAsync(
        TableReservationDbContext db, ICurrentUser user, IBranchDirectory directory, Guid? branchId, CancellationToken ct)
    {
        var tenantId = TenantScope.Require(user);
        var branch = await Scope.FilterAsync(user, directory, branchId, ct);
        var query = db.TableAreas.AsNoTracking().Where(a => a.TenantId == tenantId);
        if (branch is not null)
        {
            query = query.Where(a => a.BranchId == branch);
        }

        var rows = await query
            .OrderBy(a => a.BranchId).ThenBy(a => a.SortOrder).ThenBy(a => a.Name)
            .Select(a => new { Area = a, Count = db.DiningTables.Count(t => t.AreaId == a.Id) })
            .ToListAsync(ct);
        return rows.Select(r => AreaResponse.From(r.Area, r.Count)).ToList();
    }

    private static async Task<AreaResponse> GetAsync(Guid id, TableReservationDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var area = await FindAsync(db, user, id, ct);
        return AreaResponse.From(area, await db.DiningTables.CountAsync(t => t.AreaId == id, ct));
    }

    private static async Task<IResult> CreateAsync(
        CreateAreaRequest request, TableReservationDbContext db, ICurrentUser user, IBranchDirectory directory, CancellationToken ct)
    {
        Validate(request.Name, request.SortOrder);
        var tenantId = TenantScope.Require(user);
        var branchId = await BranchScope.ResolveAsync(user, directory, request.BranchId, requireActive: true, ct);
        await EnsureNameUniqueAsync(db, branchId, request.Name!, null, ct);

        var area = new TableArea(tenantId, branchId, request.Name!, request.SortOrder ?? 0);
        db.TableAreas.Add(area);
        await db.SaveChangesAsync(ct);

        return Results.CreatedAtRoute("GetArea", new { id = area.Id }, AreaResponse.From(area, 0));
    }

    private static async Task<AreaResponse> UpdateAsync(
        Guid id, UpdateAreaRequest request, TableReservationDbContext db, ICurrentUser user, CancellationToken ct)
    {
        Validate(request.Name, request.SortOrder);
        var area = await FindAsync(db, user, id, ct);
        await EnsureNameUniqueAsync(db, area.BranchId, request.Name!, id, ct);

        area.Update(request.Name!, request.SortOrder ?? area.SortOrder);
        await db.SaveChangesAsync(ct);
        return AreaResponse.From(area, await db.DiningTables.CountAsync(t => t.AreaId == id, ct));
    }

    private static async Task<IResult> DeleteAsync(Guid id, TableReservationDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var area = await FindAsync(db, user, id, ct);
        if (await db.DiningTables.AnyAsync(t => t.AreaId == id, ct))
        {
            throw new ConflictException("The area still has tables. Move or delete them first.", "area.has_tables");
        }

        db.TableAreas.Remove(area);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static void Validate(string? name, int? sortOrder)
    {
        var errors = new ValidationErrors();
        CommonRules.Required(errors, "Name", name, "Area name", TableArea.NameMaxLength);
        if (sortOrder is < 0 or > 9999)
        {
            errors.Add("SortOrder", "Sort order must be between 0 and 9999.");
        }

        errors.ThrowIfAny();
    }

    private static async Task EnsureNameUniqueAsync(TableReservationDbContext db, Guid branchId, string name, Guid? excludeId, CancellationToken ct)
    {
        var normalized = name.Trim().ToLower();
        if (await db.TableAreas.AnyAsync(a => a.BranchId == branchId && a.Name.ToLower() == normalized && a.Id != excludeId, ct))
        {
            throw new ConflictException($"Area '{name.Trim()}' already exists in this branch.", "area.duplicate_name");
        }
    }

    /// <summary>An area of another tenant is not found; one of another branch of the same tenant is forbidden for branch staff (BR-05).</summary>
    internal static async Task<TableArea> FindAsync(TableReservationDbContext db, ICurrentUser user, Guid id, CancellationToken ct)
    {
        var tenantId = TenantScope.Require(user);
        var area = await db.TableAreas.FirstOrDefaultAsync(a => a.Id == id && a.TenantId == tenantId, ct) ?? throw new NotFoundException("Area", id);
        BranchAccess.EnsureCanAccess(user, area.BranchId);
        return area;
    }
}

/// <summary>Branch scoping for reads: a BrandAdmin may omit the branch to see the whole brand; everyone else sees their own branch.</summary>
internal static class Scope
{
    public static async Task<Guid?> FilterAsync(ICurrentUser user, IBranchDirectory directory, Guid? requestedBranchId, CancellationToken ct)
    {
        if (user.IsInRole(AicosRoles.BrandAdmin) && requestedBranchId is null)
        {
            return null;
        }

        return await BranchScope.ResolveAsync(user, directory, requestedBranchId, requireActive: false, ct);
    }
}
