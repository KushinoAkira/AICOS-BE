using System.Net.Mail;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.IdentityOrganization.Api.Domain;
using AICOS.IdentityOrganization.Api.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AICOS.IdentityOrganization.Api.Features.StaffProvisioning;

public sealed class StaffProvisioningService(
    IdentityOrganizationDbContext dbContext,
    IPasswordHasher<UserAccount> passwordHasher,
    ILogger<StaffProvisioningService> logger)
{
    public async Task<StaffAccountResponse> CreateAsync(
        CreateStaffRequest request,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        Validate(request);
        var role = NormalizeRole(request.Role);
        EnsureCallerCanProvision(currentUser, request.BranchId, role.ApiRole);

        var branch = await dbContext.Branches
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.Id == request.BranchId &&
                    item.Status == IdentityStatuses.Active &&
                    dbContext.Tenants.Any(tenant =>
                        tenant.Id == item.TenantId && tenant.Status == IdentityStatuses.Active),
                cancellationToken)
            ?? throw new NotFoundException("Active branch", request.BranchId);

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        if (await dbContext.Users.AnyAsync(
                user => user.Email.ToLower() == normalizedEmail,
                cancellationToken))
        {
            throw new ConflictException(
                "An account with this email already exists.",
                "identity.email_exists");
        }

        var databaseRole = await dbContext.Roles
            .SingleOrDefaultAsync(
                item => item.Code.ToUpper() == role.DatabaseCode,
                cancellationToken);
        if (databaseRole is null)
        {
            databaseRole = new Role(Guid.NewGuid(), role.DatabaseCode, role.DisplayName);
            dbContext.Roles.Add(databaseRole);
        }

        var user = new UserAccount(Guid.NewGuid(), normalizedEmail, request.FullName);
        user.SetPasswordHash(passwordHasher.HashPassword(user, request.Password));
        var membership = new TenantUser(
            Guid.NewGuid(),
            branch.TenantId,
            user.Id,
            databaseRole.Id);
        var assignment = new StaffAssignment(
            Guid.NewGuid(),
            branch.Id,
            membership.Id);

        dbContext.Users.Add(user);
        dbContext.TenantUsers.Add(membership);
        dbContext.StaffAssignments.Add(assignment);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            logger.LogWarning(
                exception,
                "Provisioning conflict for email {Email} at branch {BranchId}",
                normalizedEmail,
                branch.Id);
            throw new ConflictException(
                "The staff account could not be created because its email, role, or assignment already exists.",
                "identity.provisioning_conflict");
        }

        logger.LogInformation(
            "User {ActorUserId} provisioned staff {StaffUserId} at branch {BranchId} as {Role}",
            currentUser.UserId,
            user.Id,
            branch.Id,
            role.ApiRole);

        return new StaffAccountResponse(
            user.Id,
            user.Email,
            user.FullName,
            branch.TenantId,
            branch.Id,
            branch.Code,
            branch.Name,
            role.ApiRole,
            IdentityStatuses.Active);
    }

    private static void EnsureCallerCanProvision(
        ICurrentUser currentUser,
        Guid targetBranchId,
        string targetRole)
    {
        if (currentUser.IsInRole(AicosRoles.SystemAdmin))
        {
            return;
        }

        if (!currentUser.IsInRole(AicosRoles.Manager) || currentUser.BranchId != targetBranchId)
        {
            throw new ForbiddenException("Managers can provision staff only for their assigned branch.");
        }

        if (targetRole == AicosRoles.Manager)
        {
            throw new ForbiddenException("Only a system administrator can provision a manager.");
        }
    }

    private static ProvisioningRole NormalizeRole(string role) =>
        role.Trim().Replace("_", string.Empty, StringComparison.Ordinal).ToUpperInvariant() switch
        {
            "MANAGER" => new("MANAGER", "Quản lý", AicosRoles.Manager),
            "WAITER" => new("WAITER", "Nhân viên phục vụ", AicosRoles.Waiter),
            "CASHIER" => new("CASHIER", "Thu ngân", AicosRoles.Cashier),
            "WAREHOUSESTAFF" => new("WAREHOUSE_STAFF", "Nhân viên kho", AicosRoles.WarehouseStaff),
            _ => throw new ValidationFailedException(
                "role",
                "Role must be Manager, Waiter, Cashier, or WarehouseStaff."),
        };

    private static void Validate(CreateStaffRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        var email = request.Email?.Trim() ?? string.Empty;
        if (email.Length is 0 or > 255 || !MailAddress.TryCreate(email, out _))
        {
            errors["email"] = ["A valid email of at most 255 characters is required."];
        }

        var fullName = request.FullName?.Trim() ?? string.Empty;
        if (fullName.Length is 0 or > 200)
        {
            errors["fullName"] = ["Full name is required and must not exceed 200 characters."];
        }

        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length is < 12 or > 200)
        {
            errors["password"] = ["Password must contain between 12 and 200 characters."];
        }

        if (request.BranchId == Guid.Empty)
        {
            errors["branchId"] = ["Branch is required."];
        }

        if (string.IsNullOrWhiteSpace(request.Role))
        {
            errors["role"] = ["Role is required."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationFailedException(errors);
        }
    }

    private sealed record ProvisioningRole(string DatabaseCode, string DisplayName, string ApiRole);
}
