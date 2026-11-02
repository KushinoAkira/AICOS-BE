using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Messaging;
using AICOS.Contracts.Events;
using AICOS.IdentityOrganization.Api.Domain;
using AICOS.IdentityOrganization.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.IdentityOrganization.Api.Features.Users;

/// <summary>Database-level rules shared by self-registration (UC-33) and admin account management (UC-02).</summary>
internal static class AccountRules
{
    /// <summary>409 when the email / username is already used - including by deleted accounts (never reused).</summary>
    public static async Task EnsureUniqueAsync(
        IdentityOrganizationDbContext db, string email, string? username, Guid? excludeUserId, CancellationToken ct)
    {
        var normalizedEmail = User.NormalizeLogin(email);
        var users = db.Users.IgnoreQueryFilters().Where(u => u.Id != excludeUserId);

        if (await users.AnyAsync(u => u.Email == normalizedEmail, ct))
        {
            throw new ConflictException($"Email '{normalizedEmail}' is already registered.", "user.duplicate_email");
        }

        if (!string.IsNullOrWhiteSpace(username))
        {
            var normalizedUsername = User.NormalizeLogin(username);
            if (await users.AnyAsync(u => u.Username == normalizedUsername, ct))
            {
                throw new ConflictException($"Username '{normalizedUsername}' is already taken.", "user.duplicate_username");
            }
        }
    }

    /// <summary>Staff can only be assigned to an existing, active branch of the same tenant.</summary>
    public static async Task EnsureBranchAssignableAsync(IdentityOrganizationDbContext db, Guid tenantId, Guid? branchId, CancellationToken ct)
    {
        if (branchId is null)
        {
            return;
        }

        var branch = await db.Branches.AsNoTracking().FirstOrDefaultAsync(b => b.Id == branchId && b.TenantId == tenantId, ct);
        if (branch is null)
        {
            throw new ValidationFailedException("BranchId", $"Branch '{branchId}' does not exist.");
        }

        if (branch.Status != BranchStatus.Active)
        {
            throw new ValidationFailedException("BranchId", $"Branch '{branch.Name}' is suspended; staff cannot be assigned to it.");
        }
    }

    /// <summary>Tells Customer Engagement the customer changed their name/phone. Best effort, like <see cref="PublishCustomerRegisteredAsync"/>.</summary>
    public static async Task PublishProfileUpdatedAsync(IEventBus bus, ILogger logger, User user, CancellationToken ct)
    {
        try
        {
            await bus.PublishAsync(new UserProfileUpdatedEvent(user.Id, user.FullName, user.Phone), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not publish UserProfileUpdatedEvent for user {UserId}", user.Id);
        }
    }

    /// <summary>
    /// Tells Customer Engagement to create the customer profile. Redis Pub/Sub is at-most-once: a publish failure is
    /// logged, not surfaced, because the account itself was created successfully.
    /// </summary>
    public static async Task PublishCustomerRegisteredAsync(IEventBus bus, ILogger logger, User user, Guid tenantId, CancellationToken ct)
    {
        try
        {
            await bus.PublishAsync(new CustomerRegisteredEvent(user.Id, user.FullName, user.Email, user.Phone, tenantId), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not publish CustomerRegisteredEvent for user {UserId}", user.Id);
        }
    }
}
