using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Security;
using AICOS.IdentityOrganization.Api.Domain;
using AICOS.IdentityOrganization.Api.Features.Users;
using AICOS.IdentityOrganization.Api.Infrastructure;
using AICOS.IdentityOrganization.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AICOS.IdentityOrganization.Api.Features.Auth;

/// <summary>
/// UC-01 Login, UC-33 Register (see <see cref="SignUpEndpoints"/>), refresh/logout, change password and forgot/reset password.
/// Routes: /api/v1/identity/auth. Everything is anonymous except /me and /change-password.
/// </summary>
public static class AuthEndpoints
{
    private const string LoggerCategory = "AICOS.Identity.Auth";

    public static void MapAuthEndpoints(this RouteGroupBuilder api)
    {
        var auth = api.MapGroup("/auth").WithTags("Auth");

        auth.MapSignUpEndpoints();
        auth.MapSessionEndpoints();
        auth.MapPost("/login", LoginAsync).AllowAnonymous();
        auth.MapPost("/refresh", RefreshAsync).AllowAnonymous();
        auth.MapPost("/logout", LogoutAsync).AllowAnonymous();
        auth.MapGet("/me", MeAsync).RequireAuthorization();
        auth.MapPut("/me", UpdateProfileAsync).RequireAuthorization();
        auth.MapPost("/change-password", ChangePasswordAsync).RequireAuthorization();
        auth.MapPost("/forgot-password", ForgotPasswordAsync).AllowAnonymous();
        auth.MapPost("/reset-password", ResetPasswordAsync).AllowAnonymous();
    }

    // ---------------------------------------------------------------- UC-01 Login
    private static async Task<AuthResponse> LoginAsync(
        LoginRequest request,
        HttpContext http,
        IdentityOrganizationDbContext db,
        PasswordService passwords,
        AuthTokenIssuer tokens,
        AuditLogger audit,
        IOptions<AuthOptions> options,
        TimeProvider clock,
        CancellationToken ct)
    {
        AuthRequestValidator.EnsureValid(request);
        var settings = options.Value;
        var now = clock.GetUtcNow().UtcDateTime;
        var login = User.NormalizeLogin(request.Login!);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == login || u.Username == login, ct)
            ?? throw InvalidCredentials();

        EnsureNotLocked(user, now);

        var (verified, rehashed) = passwords.Verify(user, request.Password!);
        if (!verified)
        {
            var lockedNow = user.RecordFailedLogin(now, settings.MaxFailedLoginAttempts, TimeSpan.FromMinutes(settings.LockoutMinutes));
            if (lockedNow)
            {
                audit.RecordUser("user.locked_out", user, after: new { Reason = "Too many failed login attempts", settings.LockoutMinutes });
            }

            await db.SaveChangesAsync(ct);

            if (lockedNow)
            {
                throw TemporarilyLocked(
                    $"Too many failed login attempts. The account is locked for {settings.LockoutMinutes} minutes.", user.LockoutEndUtc!.Value, now);
            }

            var remaining = settings.MaxFailedLoginAttempts - user.FailedLoginCount;
            throw new UnauthorizedException(
                $"Invalid email/username or password. {remaining} attempt(s) left before the account is locked.",
                "auth.invalid_credentials");
        }

        if (user.Status == UserStatus.PendingVerification)
        {
            throw new ForbiddenException("Verify your email with the code we sent before signing in.", "auth.email_not_verified");
        }

        await EnsureTenantActiveAsync(db, user, ct);
        await EnsureBranchActiveAsync(db, user, ct);

        if (rehashed is not null)
        {
            user.SetPassword(rehashed, user.MustChangePassword);
        }

        user.RecordSuccessfulLogin(now);
        var issued = tokens.StartSession(user, http);
        await db.SaveChangesAsync(ct);

        return AuthResponse.From(issued, UserResponse.From(user));
    }

    // ---------------------------------------------------------------- Refresh / Logout
    private static async Task<AuthResponse> RefreshAsync(
        RefreshTokenRequest request,
        IdentityOrganizationDbContext db,
        AuthTokenIssuer tokens,
        TimeProvider clock,
        CancellationToken ct)
    {
        AuthRequestValidator.EnsureValid(request);
        var now = clock.GetUtcNow().UtcDateTime;
        var hash = Secrets.Sha256(request.RefreshToken!.Trim());

        var session = await db.UserSessions.Include(s => s.User).FirstOrDefaultAsync(s => s.RefreshTokenHash == hash, ct);
        if (session?.User is null || !session.IsActive(now))
        {
            throw new UnauthorizedException("Refresh token is invalid, expired or revoked. Please log in again.", "auth.invalid_refresh_token");
        }

        var user = session.User;
        if (user.Status == UserStatus.Locked)
        {
            session.Revoke(now);
            await db.SaveChangesAsync(ct);
            throw AccountLocked();
        }

        await EnsureTenantActiveAsync(db, user, ct);
        await EnsureBranchActiveAsync(db, user, ct);

        // A customer whose chosen brand was suspended stays signed in and picks another brand.
        var activeBrand = session.ActiveTenantId is null
            ? null
            : await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == session.ActiveTenantId && t.Status == TenantStatus.Active, ct);
        session.SetActiveTenant(activeBrand?.Id);

        var issued = tokens.RotateSession(session, user);
        await db.SaveChangesAsync(ct);

        return AuthResponse.From(issued, UserResponse.From(user), activeBrand?.Code);
    }

    private static async Task<IResult> LogoutAsync(
        RefreshTokenRequest request, IdentityOrganizationDbContext db, TimeProvider clock, CancellationToken ct)
    {
        AuthRequestValidator.EnsureValid(request);
        var hash = Secrets.Sha256(request.RefreshToken!.Trim());

        // Idempotent: unknown or already revoked tokens still return 204 (nothing to reveal).
        var session = await db.UserSessions.FirstOrDefaultAsync(s => s.RefreshTokenHash == hash, ct);
        if (session is not null)
        {
            session.Revoke(clock.GetUtcNow().UtcDateTime);
            await db.SaveChangesAsync(ct);
        }

        return Results.NoContent();
    }

    // ---------------------------------------------------------------- Current user
    private static async Task<UserResponse> MeAsync(ICurrentUser currentUser, IdentityOrganizationDbContext db, CancellationToken ct) =>
        UserResponse.From(await GetCurrentUserAsync(currentUser, db, ct));

    /// <summary>UC-03: any signed-in user edits their own name, phone and username. The email and the role are not editable here.</summary>
    private static async Task<UserResponse> UpdateProfileAsync(
        UpdateProfileRequest request,
        ICurrentUser currentUser,
        IdentityOrganizationDbContext db,
        IEventBus bus,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        AuthRequestValidator.EnsureValid(request);
        var user = await GetCurrentUserAsync(currentUser, db, ct);
        await AccountRules.EnsureUniqueAsync(db, user.Email, request.Username, excludeUserId: user.Id, ct);

        user.UpdateProfile(request.FullName!, user.Email, request.Username, request.Phone);
        await db.SaveChangesAsync(ct);

        if (user.Role == AicosRoles.Customer)
        {
            await AccountRules.PublishProfileUpdatedAsync(bus, loggers.CreateLogger(LoggerCategory), user, ct);
        }

        return UserResponse.From(user);
    }

    private static async Task<IResult> ChangePasswordAsync(
        ChangePasswordRequest request,
        ICurrentUser currentUser,
        IdentityOrganizationDbContext db,
        PasswordService passwords,
        AuthTokenIssuer tokens,
        AuditLogger audit,
        CancellationToken ct)
    {
        AuthRequestValidator.EnsureValid(request);
        var user = await GetCurrentUserAsync(currentUser, db, ct);

        if (!passwords.Verify(user, request.CurrentPassword!).Verified)
        {
            throw new ValidationFailedException(nameof(request.CurrentPassword), "Current password is incorrect.");
        }

        user.SetPassword(passwords.Hash(user, request.NewPassword!), mustChangePassword: false);
        audit.RecordUser("user.password_changed", user);
        await tokens.RevokeAllSessionsAsync(user.Id, ct);
        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    // ---------------------------------------------------------------- Forgot / reset password
    private static async Task<IResult> ForgotPasswordAsync(
        ForgotPasswordRequest request,
        IdentityOrganizationDbContext db,
        IEmailSender email,
        IOptions<AuthOptions> options,
        ILoggerFactory loggers,
        TimeProvider clock,
        CancellationToken ct)
    {
        AuthRequestValidator.EnsureValid(request);
        var settings = options.Value;
        var now = clock.GetUtcNow().UtcDateTime;
        var target = User.NormalizeLogin(request.Email!);

        // Same answer whether or not the email exists, so this endpoint cannot be used to discover accounts.
        var accepted = Results.Accepted(value: new MessageResponse(
            "If an account with that email exists, a 6-digit reset code has been sent to it."));

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Email == target, ct);
        if (user is null || user.Status != UserStatus.Active)
        {
            return accepted;
        }

        var previous = await db.OtpCodes
            .Where(o => o.Target == target && o.Purpose == OtpPurposes.PasswordReset && o.ConsumedAtUtc == null)
            .ToListAsync(ct);

        if (previous.Any(o => o.CreatedAtUtc > now.AddSeconds(-settings.PasswordResetResendSeconds)))
        {
            return accepted; // anti-spam: a code was sent moments ago
        }

        previous.ForEach(o => o.Consume(now)); // only the newest code is valid

        var code = Secrets.NewNumericCode();
        db.OtpCodes.Add(new OtpCode(
            target, Secrets.Sha256(code), OtpPurposes.PasswordReset, now, now.AddMinutes(settings.PasswordResetCodeMinutes)));
        await db.SaveChangesAsync(ct);

        try
        {
            await email.SendAsync(EmailTemplates.PasswordReset(target, user.FullName, code, settings.PasswordResetCodeMinutes), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The answer must not depend on the mail server: it would tell an attacker which accounts exist. The sender already logged the failure.
            loggers.CreateLogger("AICOS.Identity.PasswordReset").LogWarning("The reset code could not be mailed ({Reason}).", ex.GetType().Name);
        }

        return accepted;
    }

    private static async Task<IResult> ResetPasswordAsync(
        ResetPasswordRequest request,
        IdentityOrganizationDbContext db,
        PasswordService passwords,
        AuthTokenIssuer tokens,
        AuditLogger audit,
        IOptions<AuthOptions> options,
        TimeProvider clock,
        CancellationToken ct)
    {
        AuthRequestValidator.EnsureValid(request);
        var settings = options.Value;
        var now = clock.GetUtcNow().UtcDateTime;
        var target = User.NormalizeLogin(request.Email!);

        var otp = await db.OtpCodes
            .Where(o => o.Target == target && o.Purpose == OtpPurposes.PasswordReset && o.ConsumedAtUtc == null)
            .OrderByDescending(o => o.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);

        if (otp is null || !otp.IsUsable(now, settings.PasswordResetMaxAttempts))
        {
            throw new BusinessRuleException("auth.reset_code_invalid", "The reset code is invalid or has expired. Request a new code.");
        }

        if (!Secrets.HashEquals(otp.CodeHash, Secrets.Sha256(request.Code!.Trim())))
        {
            otp.RecordFailedAttempt();
            await db.SaveChangesAsync(ct);
            throw new BusinessRuleException("auth.reset_code_invalid", "The reset code is invalid or has expired. Request a new code.");
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == target, ct)
            ?? throw new BusinessRuleException("auth.reset_code_invalid", "The reset code is invalid or has expired. Request a new code.");

        otp.Consume(now);
        user.SetPassword(passwords.Hash(user, request.NewPassword!), mustChangePassword: false);
        audit.RecordUser("user.password_reset", user);
        user.ClearLockout();
        await tokens.RevokeAllSessionsAsync(user.Id, ct);
        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    // ---------------------------------------------------------------- helpers
    private static UnauthorizedException InvalidCredentials() =>
        new("Invalid email/username or password.", "auth.invalid_credentials");

    internal static ForbiddenException AccountLocked() =>
        new("This account has been locked by an administrator.", "account.locked");

    private static void EnsureNotLocked(User user, DateTime now)
    {
        if (user.Status == UserStatus.Locked)
        {
            throw AccountLocked();
        }

        if (user.IsLockedOut(now))
        {
            var minutes = (int)Math.Ceiling((user.LockoutEndUtc!.Value - now).TotalMinutes);
            throw TemporarilyLocked(
                $"Too many failed login attempts. Try again in {minutes} minute(s) or reset your password.", user.LockoutEndUtc!.Value, now);
        }
    }

    /// <summary>403 <c>account.temporarily_locked</c> with <c>lockedUntilUtc</c> and <c>retryAfterSeconds</c> (also the Retry-After header) so the client can show a countdown.</summary>
    private static ForbiddenException TemporarilyLocked(string message, DateTime lockedUntilUtc, DateTime now) =>
        new(message, "account.temporarily_locked")
        {
            Extensions = new Dictionary<string, object?>
            {
                ["lockedUntilUtc"] = DateTime.SpecifyKind(lockedUntilUtc, DateTimeKind.Utc),
                ["retryAfterSeconds"] = Math.Max(1, (int)Math.Ceiling((lockedUntilUtc - now).TotalSeconds)),
            },
        };

    /// <summary>BR-07: every user of a suspended tenant is denied login.</summary>
    internal static async Task EnsureTenantActiveAsync(IdentityOrganizationDbContext db, User user, CancellationToken ct)
    {
        if (user.TenantId is null)
        {
            return; // SuperAdmin
        }

        var status = await db.Tenants.Where(t => t.Id == user.TenantId).Select(t => (TenantStatus?)t.Status).FirstOrDefaultAsync(ct);
        if (status != TenantStatus.Active)
        {
            throw NotActive(status);
        }
    }

    /// <summary>Why a brand cannot be used: waiting for approval, refused, or suspended.</summary>
    internal static ForbiddenException NotActive(TenantStatus? status) => status switch
    {
        TenantStatus.Pending => new ForbiddenException("The registration of your brand is waiting for approval. You can sign in once it is approved.", "tenant.pending_approval"),
        TenantStatus.Rejected => new ForbiddenException("The registration of your brand was refused. Contact the platform administrator.", "tenant.rejected"),
        _ => new ForbiddenException("Your brand is suspended. Contact the platform administrator.", "tenant.suspended"),
    };

    /// <summary>BR-38: staff of a suspended branch cannot sign in.</summary>
    private static async Task EnsureBranchActiveAsync(IdentityOrganizationDbContext db, User user, CancellationToken ct)
    {
        if (user.BranchId is null)
        {
            return;
        }

        var status = await db.Branches.Where(b => b.Id == user.BranchId).Select(b => (BranchStatus?)b.Status).FirstOrDefaultAsync(ct);
        if (status != BranchStatus.Active)
        {
            throw new ForbiddenException("Your branch is suspended. Contact the system administrator.", "branch.suspended");
        }
    }

    private static async Task<User> GetCurrentUserAsync(ICurrentUser currentUser, IdentityOrganizationDbContext db, CancellationToken ct)
    {
        var id = currentUser.UserId ?? throw new UnauthorizedException("Not signed in.");
        return await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct)
            ?? throw new NotFoundException("User", id); // e.g. a /dev/token token, which has no real account
    }
}
