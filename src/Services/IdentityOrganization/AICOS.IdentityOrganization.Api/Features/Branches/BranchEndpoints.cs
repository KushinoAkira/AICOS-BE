using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.IdentityOrganization.Api.Domain;
using AICOS.BuildingBlocks.Persistence;
using AICOS.IdentityOrganization.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.IdentityOrganization.Api.Features.Branches;

/// <summary>
/// UC-03 Manage Branches. Routes: /api/v1/identity/branches.
/// Only SystemAdmin can create/update/suspend; a Manager may read the single branch they are assigned to (BR-58).
/// Branches are never hard-deleted - they are suspended instead (BR-38).
/// </summary>
public static class BranchEndpoints
{
    private const int MaxPageSize = 100;

    public static void MapBranchEndpoints(this RouteGroupBuilder api)
    {
        var branches = api.MapGroup("/branches").WithTags("Branches");

        branches.MapGet("/", ListAsync).RequireAuthorization(AicosPolicies.AdminOnly);
        branches.MapGet("/active", ListActiveAsync).AllowAnonymous();
        branches.MapGet("/{id:guid}", GetAsync).RequireAuthorization(AicosPolicies.Management).WithName("GetBranch");
        branches.MapPost("/", CreateAsync).RequireAuthorization(AicosPolicies.AdminOnly);
        branches.MapPut("/{id:guid}", UpdateAsync).RequireAuthorization(AicosPolicies.AdminOnly);
        branches.MapPatch("/{id:guid}/status", ChangeStatusAsync).RequireAuthorization(AicosPolicies.AdminOnly);
    }

    private static async Task<PagedResponse<BranchResponse>> ListAsync(
        IdentityOrganizationDbContext db,
        string? search,
        BranchStatus? status,
        int page = 1,
        int pageSize = 20,
        CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var query = db.Branches.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(b =>
                b.Name.ToLower().Contains(term)
                || (b.Address != null && b.Address.ToLower().Contains(term))
                || (b.Phone != null && b.Phone.Contains(term)));
        }

        if (status is not null)
        {
            query = query.Where(b => b.Status == status);
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderBy(b => b.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new PagedResponse<BranchResponse>(items.Select(BranchResponse.From).ToList(), page, pageSize, total);
    }

    /// <summary>Public: active branches for guests/customers choosing where to view the menu or reserve a table.</summary>
    private static async Task<List<BranchResponse>> ListActiveAsync(IdentityOrganizationDbContext db, CancellationToken ct)
    {
        var branches = await db.Branches.AsNoTracking()
            .Where(b => b.Status == BranchStatus.Active)
            .OrderBy(b => b.Name)
            .ToListAsync(ct);
        return branches.Select(BranchResponse.From).ToList();
    }

    private static async Task<BranchResponse> GetAsync(
        Guid id, IdentityOrganizationDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        if (currentUser.IsInRole(AicosRoles.Manager) && currentUser.BranchId != id)
        {
            throw new ForbiddenException("Managers can only view the branch they are assigned to.");
        }

        var branch = await db.Branches.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct)
            ?? throw new NotFoundException("Branch", id);

        return BranchResponse.From(branch);
    }

    private static async Task<IResult> CreateAsync(BranchRequest request, IdentityOrganizationDbContext db, CancellationToken ct)
    {
        BranchRequestValidator.EnsureValid(request);
        await EnsureNameIsUniqueAsync(db, request.Name!, excludeId: null, ct);

        var branch = new Branch(request.Name!, request.Address, request.Phone, request.OpeningTime, request.ClosingTime);
        db.Branches.Add(branch);
        await db.SaveChangesAsync(ct);

        return Results.CreatedAtRoute("GetBranch", new { id = branch.Id }, BranchResponse.From(branch));
    }

    private static async Task<BranchResponse> UpdateAsync(
        Guid id, BranchRequest request, IdentityOrganizationDbContext db, CancellationToken ct)
    {
        BranchRequestValidator.EnsureValid(request);

        var branch = await db.Branches.FirstOrDefaultAsync(b => b.Id == id, ct)
            ?? throw new NotFoundException("Branch", id);

        await EnsureNameIsUniqueAsync(db, request.Name!, excludeId: id, ct);

        branch.Update(request.Name!, request.Address, request.Phone, request.OpeningTime, request.ClosingTime);
        await db.SaveChangesAsync(ct);

        return BranchResponse.From(branch);
    }

    private static async Task<BranchResponse> ChangeStatusAsync(
        Guid id, ChangeBranchStatusRequest request, IdentityOrganizationDbContext db, CancellationToken ct)
    {
        if (request.Status is null)
        {
            throw new ValidationFailedException(nameof(request.Status), "Status is required (Active or Suspended).");
        }

        var branch = await db.Branches.FirstOrDefaultAsync(b => b.Id == id, ct)
            ?? throw new NotFoundException("Branch", id);

        branch.ChangeStatus(request.Status.Value);
        await db.SaveChangesAsync(ct);

        return BranchResponse.From(branch);
    }

    private static async Task EnsureNameIsUniqueAsync(
        IdentityOrganizationDbContext db, string name, Guid? excludeId, CancellationToken ct)
    {
        var normalized = name.Trim().ToLower();
        var exists = await db.Branches.AnyAsync(b => b.Name.ToLower() == normalized && b.Id != excludeId, ct);
        if (exists)
        {
            throw new ConflictException($"A branch named '{name.Trim()}' already exists.", "branch.duplicate_name");
        }
    }
}
