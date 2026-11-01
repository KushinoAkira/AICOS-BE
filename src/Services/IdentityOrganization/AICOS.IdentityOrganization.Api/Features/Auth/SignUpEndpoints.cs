using System.Security.Cryptography;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;
using AICOS.IdentityOrganization.Api.Domain;
using AICOS.IdentityOrganization.Api.Features.Users;
using AICOS.IdentityOrganization.Api.Infrastructure;
using AICOS.IdentityOrganization.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AICOS.IdentityOrganization.Api.Features.Auth;

/// <summary>
/// UC-04 Sign up of a customer, by email or with Google. One account works at every brand: the customer picks a brand after signing in
/// (select-brand), which also creates their profile and loyalty wallet at that brand. By email: register (account created as pending,
/// 6-digit code mailed) then verify (account activated and signed in); resend sends a new code. With Google: one call that signs in or
/// creates the account. Routes: /api/v1/identity/auth/register, /register/verify, /register/resend, /google (anonymous), /select-brand.
/// </summary>
public static class SignUpEndpoints
{
    private const string LoggerCategory = "AICOS.Identity.SignUp";

    public static void MapSignUpEndpoints(this RouteGroupBuilder auth)
    {
        auth.MapPost("/register", RegisterAsync).AllowAnonymous();
        auth.MapPost("/register/verify", VerifyAsync).AllowAnonymous();
        auth.MapPost("/register/resend", ResendAsync).AllowAnonymous();
        auth.MapPost("/google", GoogleAsync).AllowAnonymous();
        auth.MapPost("/select-brand", SelectBrandAsync).RequireAuthorization();
        auth.MapPost("/me/delete", DeleteMyDataAsync).RequireAuthorization();
    }

    // ---------------------------------------------------------------- Step 1: create the pending account and mail the code
    private static async Task<IResult> RegisterAsync(
        RegisterRequest request,
        IdentityOrganizationDbContext db,
        PasswordService passwords,
        IEmailSender email,
        IOptions<AuthOptions> options,
        TimeProvider clock,
        CancellationToken ct)
    {
        AuthRequestValidator.EnsureValid(request);
        var address = User.NormalizeLogin(request.Email!);

        // A pending account is overwritten, so a mistyped address or a lost code never traps the email.
        var existing = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Email == address, ct);
        if (existing is not null && (existing.Status != UserStatus.PendingVerification || existing.DeletedAtUtc is not null))
        {
            throw new ConflictException($"Email '{address}' is already registered.", "user.duplicate_email");
        }

        var user = existing ?? new User(request.FullName!, address, username: null, phone: null, AicosRoles.Customer, branchId: null);
        user.UpdateProfile(request.FullName!, address, username: null, phone: null);
        user.SetPassword(passwords.Hash(user, request.Password!), mustChangePassword: false);
        if (existing is null)
        {
            user.ChangeStatus(UserStatus.PendingVerification);
            db.Users.Add(user);
        }

        return await IssueCodeAsync(db, email, options.Value, clock.GetUtcNow().UtcDateTime, user, ct);
    }

    // ---------------------------------------------------------------- Step 2: the code activates the account and signs the customer in
    private static async Task<AuthResponse> VerifyAsync(
        VerifyRegistrationRequest request,
        HttpContext http,
        IdentityOrganizationDbContext db,
        AuthTokenIssuer tokens,
        IOptions<AuthOptions> options,
        TimeProvider clock,
        CancellationToken ct)
    {
        AuthRequestValidator.EnsureValid(request);
        var settings = options.Value;
        var now = clock.GetUtcNow().UtcDateTime;
        var address = User.NormalizeLogin(request.Email!);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == address && u.Status == UserStatus.PendingVerification, ct)
            ?? throw InvalidCode();

        var otp = await db.OtpCodes
            .Where(o => o.Target == address && o.Purpose == OtpPurposes.EmailVerification && o.ConsumedAtUtc == null)
            .OrderByDescending(o => o.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);
        if (otp is null || !otp.IsUsable(now, settings.SignUpMaxAttempts))
        {
            throw InvalidCode();
        }

        if (!Secrets.HashEquals(otp.CodeHash, Secrets.Sha256(request.Code!.Trim())))
        {
            otp.RecordFailedAttempt();
            await db.SaveChangesAsync(ct);
            throw InvalidCode();
        }

        otp.Consume(now);
        user.ChangeStatus(UserStatus.Active);
        user.RecordSuccessfulLogin(now);
        var issued = tokens.StartSession(user, http);
        await db.SaveChangesAsync(ct);

        return AuthResponse.From(issued, UserResponse.From(user));
    }

    // ---------------------------------------------------------------- Resend
    private static async Task<IResult> ResendAsync(
        ResendRegistrationRequest request,
        IdentityOrganizationDbContext db,
        IEmailSender email,
        IOptions<AuthOptions> options,
        TimeProvider clock,
        CancellationToken ct)
    {
        AuthRequestValidator.EnsureValid(request);
        var settings = options.Value;
        var address = User.NormalizeLogin(request.Email!);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == address && u.Status == UserStatus.PendingVerification, ct);
        if (user is null)
        {
            // Same answer whether or not a pending sign-up exists, so this cannot be used to discover accounts.
            return Results.Accepted(value: new SignUpPendingResponse(address, settings.SignUpCodeMinutes * 60, settings.SignUpResendSeconds));
        }

        return await IssueCodeAsync(db, email, settings, clock.GetUtcNow().UtcDateTime, user, ct);
    }

    // ---------------------------------------------------------------- Google: signs in, or creates the account
    private static async Task<AuthResponse> GoogleAsync(
        GoogleLoginRequest request,
        HttpContext http,
        IdentityOrganizationDbContext db,
        PasswordService passwords,
        AuthTokenIssuer tokens,
        IGoogleTokenVerifier google,
        IOptions<GoogleAuthOptions> googleOptions,
        TimeProvider clock,
        CancellationToken ct)
    {
        AuthRequestValidator.EnsureValid(request);
        if (!googleOptions.Value.IsConfigured)
        {
            throw new BusinessRuleException("auth.google_not_configured", "Sign in with Google is not set up on this server.");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var identity = await google.VerifyAsync(request.IdToken!, ct)
            ?? throw new UnauthorizedException("The Google sign-in could not be verified. Try again.", "auth.google_invalid_token");
        if (!identity.EmailVerified)
        {
            throw new UnauthorizedException("The Google account's email is not verified.", "auth.google_email_unverified");
        }

        var address = User.NormalizeLogin(identity.Email);
        var user = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.GoogleSubject == identity.Subject || u.Email == address, ct);

        if (user is null)
        {
            var name = string.IsNullOrWhiteSpace(identity.Name) ? address.Split('@')[0] : identity.Name.Trim();
            user = new User(name.Length > User.FullNameMaxLength ? name[..User.FullNameMaxLength] : name, address, username: null, phone: null,
                AicosRoles.Customer, branchId: null);
            // Nobody knows this password: the account is entered with Google, or after a password reset.
            user.SetPassword(passwords.Hash(user, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))), mustChangePassword: false);
            user.LinkGoogle(identity.Subject);
            db.Users.Add(user);
        }
        else
        {
            if (user.DeletedAtUtc is not null || user.Role != AicosRoles.Customer
                || (user.GoogleSubject is not null && user.GoogleSubject != identity.Subject))
            {
                throw new ForbiddenException("This Google account cannot be used to sign in here.", "auth.google_not_allowed");
            }

            if (user.Status == UserStatus.Locked)
            {
                throw AuthEndpoints.AccountLocked();
            }

            // Google has proven the address, so a sign-up that was still waiting for its code is complete.
            if (user.Status == UserStatus.PendingVerification)
            {
                user.ChangeStatus(UserStatus.Active);
            }

            user.LinkGoogle(identity.Subject);
        }

        user.RecordSuccessfulLogin(now);
        var issued = tokens.StartSession(user, http);
        await db.SaveChangesAsync(ct);

        return AuthResponse.From(issued, UserResponse.From(user));
    }

    // ---------------------------------------------------------------- Choosing a brand
    /// <summary>
    /// A customer picks the brand to order from: a new access token carries that tenant, so every service keeps working inside one brand and
    /// points stay with the brand that issued them (BR-47). The first visit creates the customer's profile there. Refreshing keeps the choice.
    /// </summary>
    private static async Task<AuthResponse> SelectBrandAsync(
        SelectBrandRequest request,
        ICurrentUser currentUser,
        IdentityOrganizationDbContext db,
        AuthTokenIssuer tokens,
        IEventBus bus,
        ILoggerFactory loggers,
        TimeProvider clock,
        CancellationToken ct)
    {
        AuthRequestValidator.EnsureValid(request);
        if (!currentUser.IsInRole(AicosRoles.Customer))
        {
            throw new ForbiddenException("Only customers choose a brand.", "auth.customer_only");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var hash = Secrets.Sha256(request.RefreshToken!.Trim());
        var session = await db.UserSessions.Include(s => s.User).FirstOrDefaultAsync(s => s.RefreshTokenHash == hash && s.UserId == currentUser.UserId, ct);
        if (session?.User is null || !session.IsActive(now))
        {
            throw new UnauthorizedException("Refresh token is invalid, expired or revoked. Please log in again.", "auth.invalid_refresh_token");
        }

        var code = string.IsNullOrWhiteSpace(request.TenantCode) ? null : Tenant.NormalizeCode(request.TenantCode);
        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => code != null ? t.Code == code : t.Id == request.TenantId, ct)
            ?? throw new NotFoundException("Brand", code ?? request.TenantId?.ToString() ?? string.Empty);
        if (tenant.Status != TenantStatus.Active)
        {
            throw AuthEndpoints.NotActive(tenant.Status);
        }

        session.SetActiveTenant(tenant.Id);
        var issued = tokens.RotateSession(session, session.User);
        await db.SaveChangesAsync(ct);
        await AccountRules.PublishCustomerRegisteredAsync(bus, loggers.CreateLogger(LoggerCategory), session.User, tenant.Id, ct);

        return AuthResponse.From(issued, UserResponse.From(session.User), tenant.Code);
    }

    // ---------------------------------------------------------------- UC-66 Delete my personal data
    /// <summary>BR-57, BR-70: the account is anonymized at once and every session ends; orders and audit records stay, without the person.</summary>
    private static async Task<IResult> DeleteMyDataAsync(
        DeleteMyDataRequest request,
        ICurrentUser currentUser,
        IdentityOrganizationDbContext db,
        PasswordService passwords,
        AuthTokenIssuer tokens,
        AuditLogger audit,
        IEventBus bus,
        ILoggerFactory loggers,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (!currentUser.IsInRole(AicosRoles.Customer))
        {
            throw new ForbiddenException("Only customers can delete their data here.", "auth.customer_only");
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == currentUser.UserId, ct)
            ?? throw new NotFoundException("User", currentUser.UserId ?? Guid.Empty);
        if (user.GoogleSubject is null && (string.IsNullOrEmpty(request.Password) || !passwords.Verify(user, request.Password).Verified))
        {
            throw new ValidationFailedException(nameof(request.Password), "Password is incorrect.");
        }

        audit.RecordUser("customer.data_deleted", user);
        user.Anonymize(passwords.Hash(user, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))), clock.GetUtcNow().UtcDateTime);
        await tokens.RevokeAllSessionsAsync(user.Id, ct);
        await db.SaveChangesAsync(ct);

        try
        {
            await bus.PublishAsync(new CustomerDataErasedEvent(user.Id), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            loggers.CreateLogger(LoggerCategory).LogWarning(ex, "Could not publish CustomerDataErasedEvent for user {UserId}", user.Id);
        }

        return Results.NoContent();
    }

    // ---------------------------------------------------------------- helpers
    /// <summary>
    /// Mails a new code for the pending <paramref name="user"/> and saves it together with the user. When a code was sent less than
    /// <c>SignUpResendSeconds</c> ago, nothing is mailed and the answer says how long to wait.
    /// </summary>
    private static async Task<IResult> IssueCodeAsync(
        IdentityOrganizationDbContext db, IEmailSender email, AuthOptions settings, DateTime now, User user, CancellationToken ct)
    {
        var expiresIn = settings.SignUpCodeMinutes * 60;
        var previous = await db.OtpCodes
            .Where(o => o.Target == user.Email && o.Purpose == OtpPurposes.EmailVerification && o.ConsumedAtUtc == null)
            .ToListAsync(ct);

        var last = previous.Count == 0 ? (DateTime?)null : previous.Max(o => o.CreatedAtUtc);
        if (last is not null && last > now.AddSeconds(-settings.SignUpResendSeconds))
        {
            await db.SaveChangesAsync(ct); // the (possibly overwritten) pending account
            var wait = (int)Math.Ceiling((last.Value.AddSeconds(settings.SignUpResendSeconds) - now).TotalSeconds);
            return Results.Accepted(value: new SignUpPendingResponse(user.Email, expiresIn, Math.Max(1, wait)));
        }

        previous.ForEach(o => o.Consume(now)); // only the newest code is valid
        var code = Secrets.NewNumericCode();
        var otp = new OtpCode(user.Email, Secrets.Sha256(code), OtpPurposes.EmailVerification, now, now.AddSeconds(expiresIn));
        db.OtpCodes.Add(otp);
        await db.SaveChangesAsync(ct);

        try
        {
            await email.SendAsync(EmailTemplates.SignUpCode(user.Email, user.FullName, code, settings.SignUpCodeMinutes), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The code never reached the customer: free the slot so the next try is not held back by the resend delay.
            otp.Consume(now);
            await db.SaveChangesAsync(ct);
            throw new BusinessRuleException("auth.email_send_failed", "The verification email could not be sent. Try again in a moment.");
        }

        return Results.Accepted(value: new SignUpPendingResponse(user.Email, expiresIn, settings.SignUpResendSeconds));
    }

    private static BusinessRuleException InvalidCode() =>
        new("auth.verification_code_invalid", "The verification code is invalid or has expired. Request a new code.");
}
