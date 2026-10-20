using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Security;
using AICOS.IdentityOrganization.Api.Domain;
using AICOS.BuildingBlocks.Persistence;
using AICOS.IdentityOrganization.Api.Features.Common;
using AICOS.IdentityOrganization.Api.Infrastructure;
using AICOS.IdentityOrganization.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.IdentityOrganization.Api.Features.Users;

/// <summary>
/// UC-13 / UC-27 Manage accounts and roles. Routes: /api/v1/identity/users. Always inside the caller's tenant (BR-04).
/// BrandAdmin manages every account of the brand. BranchManager manages only the Barista and Cashier of their own
/// branch (BR-05). Accounts are soft-deleted; nobody can change the role of, lock or delete their own account.
/// </summary>
public static class UserEndpoints
{
    private const int MaxPageSize = 100;

    /// <summary>Roles a BranchManager may create or edit.</summary>
    private static readonly string[] BranchManagerRoles = [AicosRoles.Barista, AicosRoles.Cashier];

    public static void MapUserEndpoints(this RouteGroupBuilder api)
    {
        var users = api.MapGroup("/users").WithTags("Users").RequireAuthorization(AicosPolicies.Management);

        users.MapGet("/", ListAsync);
        users.MapGet("/{id:guid}", GetAsync).WithName("GetUser");
        users.MapPost("/", CreateAsync);
        users.MapPut("/{id:guid}", UpdateAsync);
        users.MapPatch("/{id:guid}/status", ChangeStatusAsync);
        users.MapDelete("/{id:guid}", DeleteAsync);
    }

    /// <summary>The accounts the caller may see and manage: their tenant, narrowed to one branch's staff for a BranchManager.</summary>
    private static IQueryable<User> Scope(IdentityOrganizationDbContext db, ICurrentUser currentUser)
    {
        var tenantId = Tenancy.Require(currentUser);
        var query = db.Users.Where(u => u.TenantId == tenantId);

        if (currentUser.IsInRole(AicosRoles.BranchManager))
        {
            var branchId = currentUser.BranchId ?? throw new ForbiddenException("Your account is not assigned to a branch.");
            query = query.Where(u => u.BranchId == branchId && (u.Role == AicosRoles.Barista || u.Role == AicosRoles.Cashier));
        }

        return query;
    }

    /// <summary>BranchManager can only hand out the staff roles of their own branch; BrandAdmin is unrestricted.</summary>
    private static (string Role, Guid? BranchId) ResolveTarget(ICurrentUser currentUser, string? requestedRole, Guid? requestedBranchId)
    {
        var role = UserRequestValidator.CanonicalRole(requestedRole) ?? requestedRole ?? string.Empty;
        if (!currentUser.IsInRole(AicosRoles.BranchManager))
        {
            return (role, requestedBranchId);
        }

        var own = currentUser.BranchId ?? throw new ForbiddenException("Your account is not assigned to a branch.");
        if (!BranchManagerRoles.Contains(role))
        {
            throw new ForbiddenException("A branch manager can only manage Barista and Cashier accounts.", "user.role_not_allowed");
        }

        if (requestedBranchId is not null && requestedBranchId != own)
        {
            throw new ForbiddenException("You can only manage staff of your own branch.", "branch.forbidden");
        }

        return (role, own);
    }

    private static async Task<PagedResponse<UserResponse>> ListAsync(
        IdentityOrganizationDbContext db,
        ICurrentUser currentUser,
        string? search,
        string? role,
        UserStatus? status,
        Guid? branchId,
        int page = 1,
        int pageSize = 20,
        CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var query = Scope(db, currentUser).AsNoTracking();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(u =>
                u.FullName.ToLower().Contains(term)
                || u.Email.Contains(term)
                || (u.Username != null && u.Username.Contains(term))
                || (u.Phone != null && u.Phone.Contains(term)));
        }

        if (!string.IsNullOrWhiteSpace(role))
        {
            var canonical = UserRequestValidator.CanonicalRole(role) ?? role;
            query = query.Where(u => u.Role == canonical);
        }

        if (status is not null)
        {
            query = query.Where(u => u.Status == status);
        }

        if (branchId is not null)
        {
            query = query.Where(u => u.BranchId == branchId);
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(u => u.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new PagedResponse<UserResponse>(items.Select(UserResponse.From).ToList(), page, pageSize, total);
    }

    private static async Task<UserResponse> GetAsync(Guid id, IdentityOrganizationDbContext db, ICurrentUser currentUser, CancellationToken ct) =>
        UserResponse.From(await FindAsync(db, currentUser, id, ct));

    private static async Task<IResult> CreateAsync(
        CreateUserRequest request,
        IdentityOrganizationDbContext db,
        ICurrentUser currentUser,
        PasswordService passwords,
        AuditLogger audit,
        IEventBus bus,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var tenantId = Tenancy.Require(currentUser);
        var (role, branchId) = ResolveTarget(currentUser, request.Role, request.BranchId);
        request = request with { Role = role, BranchId = branchId };

        UserRequestValidator.EnsureValid(request);
        await AccountRules.EnsureUniqueAsync(db, request.Email!, request.Username, excludeUserId: null, ct);
        await AccountRules.EnsureBranchAssignableAsync(db, tenantId, request.BranchId, ct);

        var canonicalRole = UserRequestValidator.CanonicalRole(request.Role)!;
        var user = new User(request.FullName!, request.Email!, request.Username, request.Phone, canonicalRole, request.BranchId, tenantId);
        user.SetPassword(passwords.Hash(user, request.Password!), mustChangePassword: true);

        db.Users.Add(user);
        audit.RecordUser("user.created", user, after: AuditSnapshots.Of(user));
        await db.SaveChangesAsync(ct);

        if (canonicalRole == AicosRoles.Customer)
        {
            await AccountRules.PublishCustomerRegisteredAsync(bus, loggers.CreateLogger("AICOS.Identity.Users"), user, ct);
        }

        return Results.CreatedAtRoute("GetUser", new { id = user.Id }, UserResponse.From(user));
    }

    private static async Task<UserResponse> UpdateAsync(
        Guid id,
        UpdateUserRequest request,
        IdentityOrganizationDbContext db,
        AuthTokenIssuer tokens,
        AuditLogger audit,
        ICurrentUser currentUser,
        CancellationToken ct)
    {
        var tenantId = Tenancy.Require(currentUser);
        var user = await FindAsync(db, currentUser, id, ct);

        var (targetRole, targetBranch) = ResolveTarget(currentUser, request.Role, request.BranchId);
        request = request with { Role = targetRole, BranchId = targetBranch };

        UserRequestValidator.EnsureValid(request);
        var role = UserRequestValidator.CanonicalRole(request.Role)!;

        if (id == currentUser.UserId && role != user.Role)
        {
            throw new ConflictException("You cannot change the role of your own account.", "user.self_action");
        }

        await AccountRules.EnsureUniqueAsync(db, request.Email!, request.Username, excludeUserId: id, ct);
        if (request.BranchId != user.BranchId)
        {
            await AccountRules.EnsureBranchAssignableAsync(db, tenantId, request.BranchId, ct);
        }

        // "Role changes take effect on next login": end existing sessions so the new role is in the next token.
        if (role != user.Role || request.BranchId != user.BranchId)
        {
            await tokens.RevokeAllSessionsAsync(user.Id, ct);
        }

        var before = AuditSnapshots.Of(user);
        user.UpdateProfile(request.FullName!, request.Email!, request.Username, request.Phone);
        user.AssignRole(role, request.BranchId);
        audit.RecordUser("user.updated", user, before, AuditSnapshots.Of(user));
        await db.SaveChangesAsync(ct);

        return UserResponse.From(user);
    }

    private static async Task<UserResponse> ChangeStatusAsync(
        Guid id,
        ChangeUserStatusRequest request,
        IdentityOrganizationDbContext db,
        AuthTokenIssuer tokens,
        AuditLogger audit,
        ICurrentUser currentUser,
        CancellationToken ct)
    {
        if (request.Status is null)
        {
            throw new ValidationFailedException(nameof(request.Status), "Status is required (Active or Locked).");
        }

        if (id == currentUser.UserId)
        {
            throw new ConflictException("You cannot change the status of your own account.", "user.self_action");
        }

        var user = await FindAsync(db, currentUser, id, ct);
        if (request.Status == UserStatus.Locked)
        {
            await tokens.RevokeAllSessionsAsync(user.Id, ct);
        }

        var before = AuditSnapshots.Of(user);
        user.ChangeStatus(request.Status.Value);
        audit.RecordUser("user.status_changed", user, before, AuditSnapshots.Of(user));
        await db.SaveChangesAsync(ct);

        return UserResponse.From(user);
    }

    private static async Task<IResult> DeleteAsync(
        Guid id,
        IdentityOrganizationDbContext db,
        AuthTokenIssuer tokens,
        AuditLogger audit,
        ICurrentUser currentUser,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (id == currentUser.UserId)
        {
            throw new ConflictException("You cannot delete your own account.", "user.self_action");
        }

        var user = await FindAsync(db, currentUser, id, ct);
        await tokens.RevokeAllSessionsAsync(user.Id, ct);
        audit.RecordUser("user.deleted", user, AuditSnapshots.Of(user));
        user.MarkDeleted(clock.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    private static async Task<User> FindAsync(IdentityOrganizationDbContext db, ICurrentUser currentUser, Guid id, CancellationToken ct) =>
        await Scope(db, currentUser).FirstOrDefaultAsync(u => u.Id == id, ct) ?? throw new NotFoundException("User", id);
}
