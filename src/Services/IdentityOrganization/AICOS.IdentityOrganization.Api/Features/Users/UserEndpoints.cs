using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Security;
using AICOS.IdentityOrganization.Api.Domain;
using AICOS.BuildingBlocks.Persistence;
using AICOS.IdentityOrganization.Api.Infrastructure;
using AICOS.IdentityOrganization.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.IdentityOrganization.Api.Features.Users;

/// <summary>
/// UC-02 Manage User Accounts and Roles. Routes: /api/v1/identity/users. SystemAdmin only.
/// Accounts are soft-deleted. An admin cannot change the role of, lock or delete their own account
/// (this also guarantees at least one SystemAdmin always remains).
/// </summary>
public static class UserEndpoints
{
    private const int MaxPageSize = 100;

    public static void MapUserEndpoints(this RouteGroupBuilder api)
    {
        var users = api.MapGroup("/users").WithTags("Users").RequireAuthorization(AicosPolicies.AdminOnly);

        users.MapGet("/", ListAsync);
        users.MapGet("/{id:guid}", GetAsync).WithName("GetUser");
        users.MapPost("/", CreateAsync);
        users.MapPut("/{id:guid}", UpdateAsync);
        users.MapPatch("/{id:guid}/status", ChangeStatusAsync);
        users.MapDelete("/{id:guid}", DeleteAsync);
    }

    private static async Task<PagedResponse<UserResponse>> ListAsync(
        IdentityOrganizationDbContext db,
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

        var query = db.Users.AsNoTracking();

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

    private static async Task<UserResponse> GetAsync(Guid id, IdentityOrganizationDbContext db, CancellationToken ct) =>
        UserResponse.From(await FindAsync(db, id, ct));

    private static async Task<IResult> CreateAsync(
        CreateUserRequest request,
        IdentityOrganizationDbContext db,
        PasswordService passwords,
        IEventBus bus,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        UserRequestValidator.EnsureValid(request);
        await AccountRules.EnsureUniqueAsync(db, request.Email!, request.Username, excludeUserId: null, ct);
        await AccountRules.EnsureBranchAssignableAsync(db, request.BranchId, ct);

        var role = UserRequestValidator.CanonicalRole(request.Role)!;
        var user = new User(request.FullName!, request.Email!, request.Username, request.Phone, role, request.BranchId);
        user.SetPassword(passwords.Hash(user, request.Password!), mustChangePassword: true);

        db.Users.Add(user);
        await db.SaveChangesAsync(ct);

        if (role == AicosRoles.Customer)
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
        ICurrentUser currentUser,
        CancellationToken ct)
    {
        UserRequestValidator.EnsureValid(request);
        var user = await FindAsync(db, id, ct);
        var role = UserRequestValidator.CanonicalRole(request.Role)!;

        if (id == currentUser.UserId && role != user.Role)
        {
            throw new ConflictException("You cannot change the role of your own account.", "user.self_action");
        }

        await AccountRules.EnsureUniqueAsync(db, request.Email!, request.Username, excludeUserId: id, ct);
        if (request.BranchId != user.BranchId)
        {
            await AccountRules.EnsureBranchAssignableAsync(db, request.BranchId, ct);
        }

        // "Role changes take effect on next login": end existing sessions so the new role is in the next token.
        if (role != user.Role || request.BranchId != user.BranchId)
        {
            await tokens.RevokeAllSessionsAsync(user.Id, ct);
        }

        user.UpdateProfile(request.FullName!, request.Email!, request.Username, request.Phone);
        user.AssignRole(role, request.BranchId);
        await db.SaveChangesAsync(ct);

        return UserResponse.From(user);
    }

    private static async Task<UserResponse> ChangeStatusAsync(
        Guid id,
        ChangeUserStatusRequest request,
        IdentityOrganizationDbContext db,
        AuthTokenIssuer tokens,
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

        var user = await FindAsync(db, id, ct);
        if (request.Status == UserStatus.Locked)
        {
            await tokens.RevokeAllSessionsAsync(user.Id, ct);
        }

        user.ChangeStatus(request.Status.Value);
        await db.SaveChangesAsync(ct);

        return UserResponse.From(user);
    }

    private static async Task<IResult> DeleteAsync(
        Guid id,
        IdentityOrganizationDbContext db,
        AuthTokenIssuer tokens,
        ICurrentUser currentUser,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (id == currentUser.UserId)
        {
            throw new ConflictException("You cannot delete your own account.", "user.self_action");
        }

        var user = await FindAsync(db, id, ct);
        await tokens.RevokeAllSessionsAsync(user.Id, ct);
        user.MarkDeleted(clock.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    private static async Task<User> FindAsync(IdentityOrganizationDbContext db, Guid id, CancellationToken ct) =>
        await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct) ?? throw new NotFoundException("User", id);
}
