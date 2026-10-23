using System.Net.Mail;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.IdentityOrganization.Api.Domain;
using AICOS.IdentityOrganization.Api.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AICOS.IdentityOrganization.Api.Features.Authentication;

public sealed class AuthenticationService(
    IdentityOrganizationDbContext dbContext,
    IPasswordHasher<UserAccount> passwordHasher,
    JwtTokenService tokenService,
    ILogger<AuthenticationService> logger)
{
    public async Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        Validate(request);
        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        var assignments = await dbContext.StaffAssignments
            .AsNoTracking()
            .Include(assignment => assignment.Branch)
            .Include(assignment => assignment.TenantUser)
                .ThenInclude(membership => membership.User)
            .Include(assignment => assignment.TenantUser)
                .ThenInclude(membership => membership.Role)
            .Where(assignment =>
                assignment.Status == IdentityStatuses.Active &&
                assignment.Branch.Status == IdentityStatuses.Active &&
                assignment.TenantUser.Status == IdentityStatuses.Active &&
                assignment.TenantUser.User.Status == IdentityStatuses.Active &&
                assignment.TenantUser.User.Email.ToLower() == normalizedEmail)
            .OrderBy(assignment => assignment.Branch.Code)
            .ToListAsync(cancellationToken);

        if (assignments.Count == 0 || !PasswordMatches(assignments[0].TenantUser.User, request.Password))
        {
            logger.LogWarning("Rejected login for {Email}", normalizedEmail);
            throw new UnauthorizedException("Email or password is incorrect.", "auth.invalid_credentials");
        }

        if (request.BranchId is null && assignments.Count > 1)
        {
            return new LoginResponse(
                RequiresBranchSelection: true,
                AccessToken: null,
                ExpiresAtUtc: null,
                Staff: null,
                Branches: assignments.Select(ToBranchOption).ToList());
        }

        var assignment = request.BranchId is null
            ? assignments[0]
            : assignments.FirstOrDefault(item => item.BranchId == request.BranchId);
        if (assignment is null)
        {
            logger.LogWarning("Rejected branch {BranchId} for {Email}", request.BranchId, normalizedEmail);
            throw new UnauthorizedException(
                "The account is not assigned to this branch.",
                "auth.branch_not_assigned");
        }

        var role = MapRole(assignment.TenantUser.Role.Code);
        var user = assignment.TenantUser.User;
        var (token, expiresAtUtc) = tokenService.CreateAccessToken(
            user.Id,
            user.FullName,
            role,
            assignment.BranchId);

        logger.LogInformation(
            "User {UserId} signed in to branch {BranchId} as {Role}",
            user.Id,
            assignment.BranchId,
            role);

        return new LoginResponse(
            RequiresBranchSelection: false,
            AccessToken: token,
            ExpiresAtUtc: expiresAtUtc,
            Staff: new StaffProfileResponse(
                user.Id,
                user.FullName,
                user.Email,
                role,
                assignment.TenantUser.TenantId,
                assignment.BranchId,
                assignment.Branch.Code,
                assignment.Branch.Name),
            Branches: assignments.Select(ToBranchOption).ToList());
    }

    private bool PasswordMatches(UserAccount user, string password) =>
        user.PasswordHash is not null &&
        passwordHasher.VerifyHashedPassword(user, user.PasswordHash, password) !=
        PasswordVerificationResult.Failed;

    private static BranchOptionResponse ToBranchOption(StaffAssignment assignment) =>
        new(assignment.Branch.Id, assignment.Branch.Code, assignment.Branch.Name);

    private static string MapRole(string code) => code.Trim().ToUpperInvariant() switch
    {
        "SYSTEM_ADMIN" or "SYSTEMADMIN" => AicosRoles.SystemAdmin,
        "MANAGER" => AicosRoles.Manager,
        "WAITER" => AicosRoles.Waiter,
        "CASHIER" => AicosRoles.Cashier,
        "WAREHOUSE_STAFF" or "WAREHOUSESTAFF" => AicosRoles.WarehouseStaff,
        _ => throw new ForbiddenException("The assigned role cannot access the Staff application."),
    };

    private static void Validate(LoginRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Email) ||
            !MailAddress.TryCreate(request.Email.Trim(), out _))
        {
            errors["email"] = ["A valid email is required."];
        }

        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length > 200)
        {
            errors["password"] = ["Password is required and must not exceed 200 characters."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationFailedException(errors);
        }
    }
}
