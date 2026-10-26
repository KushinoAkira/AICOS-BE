using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.IdentityOrganization.Api.Domain;
using AICOS.BuildingBlocks.Persistence;
using AICOS.IdentityOrganization.Api.Features.Common;
using AICOS.IdentityOrganization.Api.Features.Subscriptions;
using AICOS.IdentityOrganization.Api.Infrastructure;
using AICOS.IdentityOrganization.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.IdentityOrganization.Api.Features.Branches;

/// <summary>
/// UC-12 Manage Branches. Routes: /api/v1/identity/branches. Every branch belongs to the caller's tenant (BR-04).
/// Only BrandAdmin can create/update/suspend; a BranchManager may read the single branch they are assigned to (BR-05).
/// Branches are never hard-deleted - they are suspended instead (BR-08).
/// </summary>
public static class BranchEndpoints
{
    private const int MaxPageSize = 100;

    public static void MapBranchEndpoints(this RouteGroupBuilder api)
    {
        var branches = api.MapGroup("/branches").WithTags("Branches");

        branches.MapGet("/", ListAsync).RequireAuthorization(AicosPolicies.AdminOnly);
        branches.MapGet("/active", ListActiveAsync).AllowAnonymous();
        // Any signed-in user of the brand may read a branch (name, address, status): a customer ordering for pickup, and the services that check a branch for them.
        branches.MapGet("/{id:guid}", GetAsync).RequireAuthorization().WithName("GetBranch");
        branches.MapPost("/", CreateAsync).RequireAuthorization(AicosPolicies.AdminOnly);
        branches.MapPut("/{id:guid}", UpdateAsync).RequireAuthorization(AicosPolicies.AdminOnly);
        branches.MapPatch("/{id:guid}/status", ChangeStatusAsync).RequireAuthorization(AicosPolicies.AdminOnly);
    }

    private static async Task<PagedResponse<BranchResponse>> ListAsync(
        IdentityOrganizationDbContext db,
        ICurrentUser currentUser,
        string? search,
        BranchStatus? status,
        int page = 1,
        int pageSize = 20,
        CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var tenantId = Tenancy.Require(currentUser);

        var query = db.Branches.AsNoTracking().Where(b => b.TenantId == tenantId);

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

    /// <summary>Public: active branches of one brand (<paramref name="tenantCode"/>) for guests/customers choosing where to order.</summary>
    /// <summary>
    /// The active branches of a brand. A visitor names the brand with <c>tenantCode</c>; a signed-in user of a brand may leave it out and gets the
    /// branches of their own brand (the web app lists them for pickers in the admin screens).
    /// </summary>
    private static async Task<List<BranchResponse>> ListActiveAsync(string? tenantCode, IdentityOrganizationDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        Guid? ownTenant = string.IsNullOrWhiteSpace(tenantCode) ? currentUser.TenantId : null;
        if (string.IsNullOrWhiteSpace(tenantCode) && ownTenant is null)
        {
            throw new ValidationFailedException(nameof(tenantCode), "tenantCode (the brand identifier) is required.");
        }

        var code = string.IsNullOrWhiteSpace(tenantCode) ? null : Tenant.NormalizeCode(tenantCode);
        var branches = await db.Branches.AsNoTracking()
            .Where(b => b.Status == BranchStatus.Active
                && db.Tenants.Any(t => t.Id == b.TenantId && (ownTenant != null ? t.Id == ownTenant : t.Code == code) && t.Status == TenantStatus.Active))
            .OrderBy(b => b.Name)
            .ToListAsync(ct);
        return branches.Select(BranchResponse.From).ToList();
    }


    private static async Task<BranchResponse> GetAsync(
        Guid id, IdentityOrganizationDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        if (currentUser.IsInRole(AicosRoles.BranchManager) && currentUser.BranchId != id)
        {
            throw new ForbiddenException("Branch managers can only view the branch they are assigned to.");
        }

        var tenantId = Tenancy.Require(currentUser);
        var branch = await db.Branches.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id && b.TenantId == tenantId, ct)
            ?? throw new NotFoundException("Branch", id);

        return BranchResponse.From(branch);
    }

    private static async Task<IResult> CreateAsync(
        BranchRequest request, IdentityOrganizationDbContext db, ICurrentUser currentUser, AuditLogger audit, CancellationToken ct)
    {
        BranchRequestValidator.EnsureValid(request);
        var tenantId = Tenancy.Require(currentUser);
        await EnsureNameIsUniqueAsync(db, tenantId, request.Name!, excludeId: null, ct);
        await SubscriptionRules.EnsureCanAddBranchAsync(db, tenantId, ct);

        var branch = new Branch(tenantId, request.Name!, request.Address, request.Phone, request.OpeningTime, request.ClosingTime);
        db.Branches.Add(branch);
        audit.Record(AuditScope.Tenant, tenantId, "branch.created", "Branch", branch.Id, after: AuditSnapshots.Of(branch));
        await db.SaveChangesAsync(ct);

        return Results.CreatedAtRoute("GetBranch", new { id = branch.Id }, BranchResponse.From(branch));
    }

    private static async Task<BranchResponse> UpdateAsync(
        Guid id, BranchRequest request, IdentityOrganizationDbContext db, ICurrentUser currentUser, AuditLogger audit, CancellationToken ct)
    {
        BranchRequestValidator.EnsureValid(request);
        var tenantId = Tenancy.Require(currentUser);

        var branch = await db.Branches.FirstOrDefaultAsync(b => b.Id == id && b.TenantId == tenantId, ct)
            ?? throw new NotFoundException("Branch", id);

        await EnsureNameIsUniqueAsync(db, tenantId, request.Name!, excludeId: id, ct);

        var before = AuditSnapshots.Of(branch);
        branch.Update(request.Name!, request.Address, request.Phone, request.OpeningTime, request.ClosingTime);
        audit.Record(AuditScope.Tenant, tenantId, "branch.updated", "Branch", branch.Id, before, AuditSnapshots.Of(branch));
        await db.SaveChangesAsync(ct);

        return BranchResponse.From(branch);
    }

    private static async Task<BranchResponse> ChangeStatusAsync(
        Guid id, ChangeBranchStatusRequest request, IdentityOrganizationDbContext db, ICurrentUser currentUser, AuditLogger audit, CancellationToken ct)
    {
        if (request.Status is null)
        {
            throw new ValidationFailedException(nameof(request.Status), "Status is required (Active or Suspended).");
        }

        var tenantId = Tenancy.Require(currentUser);
        var branch = await db.Branches.FirstOrDefaultAsync(b => b.Id == id && b.TenantId == tenantId, ct)
            ?? throw new NotFoundException("Branch", id);
        if (request.Status == BranchStatus.Active && branch.Status != BranchStatus.Active)
        {
            await SubscriptionRules.EnsureCanAddBranchAsync(db, tenantId, ct); // re-activating counts like adding one
        }

        var before = AuditSnapshots.Of(branch);
        branch.ChangeStatus(request.Status.Value);
        audit.Record(AuditScope.Tenant, tenantId, "branch.status_changed", "Branch", branch.Id, before, AuditSnapshots.Of(branch));
        await db.SaveChangesAsync(ct);

        return BranchResponse.From(branch);
    }

    private static async Task EnsureNameIsUniqueAsync(
        IdentityOrganizationDbContext db, Guid tenantId, string name, Guid? excludeId, CancellationToken ct)
    {
        var normalized = name.Trim().ToLower();
        var exists = await db.Branches.AnyAsync(b => b.TenantId == tenantId && b.Name.ToLower() == normalized && b.Id != excludeId, ct);
        if (exists)
        {
            throw new ConflictException($"A branch named '{name.Trim()}' already exists.", "branch.duplicate_name");
        }
    }
}
