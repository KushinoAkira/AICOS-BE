using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Persistence;
using AICOS.BuildingBlocks.Security;
using AICOS.IdentityOrganization.Api.Domain;
using AICOS.IdentityOrganization.Api.Features.Subscriptions;
using AICOS.IdentityOrganization.Api.Features.Users;
using AICOS.IdentityOrganization.Api.Infrastructure;
using AICOS.IdentityOrganization.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.IdentityOrganization.Api.Features.Tenants;

/// <summary>
/// UC-05 Manage Tenants (Brands). Routes: /api/v1/identity/tenants. SuperAdmin only.
/// Creating a tenant also creates its first Brand Admin (FR-02). Tenants are suspended, never deleted (BR-07).
/// </summary>
public static class TenantEndpoints
{
    private const int MaxPageSize = 100;

    public static void MapTenantEndpoints(this RouteGroupBuilder api)
    {
        var tenants = api.MapGroup("/tenants").WithTags("Tenants").RequireAuthorization(AicosPolicies.PlatformAdmin);

        tenants.MapGet("/", ListAsync);
        tenants.MapGet("/{id:guid}", GetAsync).WithName("GetTenant");
        tenants.MapPost("/", CreateAsync);
        tenants.MapPut("/{id:guid}", UpdateAsync);
        tenants.MapPatch("/{id:guid}/status", ChangeStatusAsync);
    }

    private static async Task<PagedResponse<TenantResponse>> ListAsync(
        IdentityOrganizationDbContext db, string? search, TenantStatus? status, int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var query = db.Tenants.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(t => t.Name.ToLower().Contains(term) || t.Code.Contains(term));
        }

        if (status is not null)
        {
            query = query.Where(t => t.Status == status);
        }

        var total = await query.CountAsync(ct);
        var items = await query.OrderBy(t => t.Name).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResponse<TenantResponse>(items.Select(TenantResponse.From).ToList(), page, pageSize, total);
    }

    private static async Task<TenantResponse> GetAsync(Guid id, IdentityOrganizationDbContext db, CancellationToken ct) =>
        TenantResponse.From(await FindAsync(db, id, ct));

    private static async Task<IResult> CreateAsync(
        CreateTenantRequest request, IdentityOrganizationDbContext db, PasswordService passwords, AuditLogger audit, TimeProvider clock, CancellationToken ct)
    {
        TenantRequestValidator.EnsureValid(request);

        var code = Tenant.NormalizeCode(request.Code!);
        if (await db.Tenants.AnyAsync(t => t.Code == code, ct))
        {
            throw new ConflictException($"Tenant code '{code}' is already used.", "tenant.duplicate_code");
        }

        var admin = request.FirstAdmin!;
        await AccountRules.EnsureUniqueAsync(db, admin.Email!, username: null, excludeUserId: null, ct);

        var tenant = new Tenant(request.Name!, code);
        var brandAdmin = new User(admin.FullName!, admin.Email!, username: null, admin.Phone, AicosRoles.BrandAdmin, branchId: null, tenant.Id);
        brandAdmin.SetPassword(passwords.Hash(brandAdmin, admin.Password!), mustChangePassword: true);

        db.Tenants.Add(tenant);
        db.Users.Add(brandAdmin);
        audit.Record(AuditScope.Platform, tenant.Id, "tenant.created", "Tenant", tenant.Id, after: AuditSnapshots.Of(tenant));

        if (request.PlanId is not null)
        {
            await TenantSubscriptionEndpoints.AssignPlanAsync(db, audit, tenant, request.PlanId.Value, startDate: null, clock, ct);
        }

        await db.SaveChangesAsync(ct);

        return Results.CreatedAtRoute("GetTenant", new { id = tenant.Id }, TenantResponse.From(tenant));
    }

    private static async Task<TenantResponse> UpdateAsync(
        Guid id, UpdateTenantRequest request, IdentityOrganizationDbContext db, AuditLogger audit, CancellationToken ct)
    {
        TenantRequestValidator.EnsureValid(request);
        var tenant = await FindAsync(db, id, ct);
        var before = AuditSnapshots.Of(tenant);
        tenant.Update(request.Name!);
        audit.Record(AuditScope.Platform, tenant.Id, "tenant.updated", "Tenant", tenant.Id, before, AuditSnapshots.Of(tenant));
        await db.SaveChangesAsync(ct);
        return TenantResponse.From(tenant);
    }

    /// <summary>BR-07: suspending a tenant also ends every session of its users, so access stops immediately.</summary>
    private static async Task<TenantResponse> ChangeStatusAsync(
        Guid id, ChangeTenantStatusRequest request, IdentityOrganizationDbContext db, AuditLogger audit, TimeProvider clock, CancellationToken ct)
    {
        if (request.Status is null)
        {
            throw new ValidationFailedException(nameof(request.Status), "Status is required (Active or Suspended).");
        }

        if (request.Status is TenantStatus.Pending or TenantStatus.Rejected)
        {
            throw new ValidationFailedException(nameof(request.Status), "Use approve or reject for a registration; the status here is Active or Suspended.");
        }

        var tenant = await FindAsync(db, id, ct);
        if (tenant.Status is TenantStatus.Pending or TenantStatus.Rejected)
        {
            throw new ConflictException("A registration is decided with approve or reject.", "tenant.use_review");
        }

        var before = AuditSnapshots.Of(tenant);
        tenant.ChangeStatus(request.Status.Value);
        audit.Record(AuditScope.Platform, tenant.Id, "tenant.status_changed", "Tenant", tenant.Id, before, AuditSnapshots.Of(tenant));

        if (request.Status == TenantStatus.Suspended)
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var sessions = await db.UserSessions
                .Where(s => s.RevokedAtUtc == null && s.User!.TenantId == id)
                .ToListAsync(ct);
            sessions.ForEach(s => s.Revoke(now));
        }

        await db.SaveChangesAsync(ct);
        return TenantResponse.From(tenant);
    }

    private static async Task<Tenant> FindAsync(IdentityOrganizationDbContext db, Guid id, CancellationToken ct) =>
        await db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException("Tenant", id);
}
