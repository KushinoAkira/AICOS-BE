using System.Text.RegularExpressions;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Messaging;
using AICOS.Contracts.Events;
using AICOS.BuildingBlocks.Persistence;
using AICOS.BuildingBlocks.Security;
using AICOS.BuildingBlocks.Validation;
using AICOS.IdentityOrganization.Api.Domain;
using AICOS.IdentityOrganization.Api.Features.Common;
using AICOS.IdentityOrganization.Api.Features.Users;
using AICOS.IdentityOrganization.Api.Infrastructure;
using AICOS.IdentityOrganization.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AICOS.IdentityOrganization.Api.Features.Tenants;

/// <summary>POST /auth/register-brand. The owner picks the brand code (the identifier of the brand) and the password of the first Brand Admin.</summary>
public sealed record RegisterBrandRequest(
    string? BrandName, string? Code, string? OwnerFullName, string? Email, string? Phone, string? Password, string? ConfirmPassword, string? Description);

public sealed record RegisterBrandResponse(Guid TenantId, string Code, TenantStatus Status, string Message);

public sealed record RejectTenantRequest(string? Reason);

/// <summary>PUT /brand/profile: what the public page of the brand shows. <c>LogoUrl</c> is the link of an image the front end already stored.</summary>
public sealed record BrandProfileRequest(string? Description, string? LogoUrl, string? PrimaryColor, string? SecondaryColor);

public sealed record BrandProfileResponse(Guid Id, string Name, string Code, TenantStatus Status, string? Description, string? LogoUrl, string? PrimaryColor, string? SecondaryColor)
{
    public static BrandProfileResponse From(Tenant t) => new(t.Id, t.Name, t.Code, t.Status, t.Description, t.LogoUrl, t.PrimaryColor, t.SecondaryColor);
}

public sealed record PublicBranchResponse(Guid Id, string Name, string? Address, string? Phone, TimeOnly? OpeningTime, TimeOnly? ClosingTime);

public sealed record PublicBrandResponse(
    string Name, string Code, string? Description, string? LogoUrl, string? PrimaryColor, string? SecondaryColor, IReadOnlyList<PublicBranchResponse> Branches);

/// <summary>One row of the brand picker customers see after signing in.</summary>
public sealed record PublicBrandCardResponse(Guid Id, string Code, string Name, string? Description, string? LogoUrl, string? PrimaryColor, int BranchCount);

/// <summary>POST /public/contact. <c>Website</c> is a hidden field of the form: a person leaves it empty, a bot fills it.</summary>
public sealed record ContactRequest(string? FullName, string? Email, string? Phone, string? Subject, string? Message, string? Website = null);

public sealed record ContactMessageResponse(Guid Id, string FullName, string Email, string? Phone, string? Subject, string Message, ContactMessageStatus Status, DateTime CreatedAtUtc, DateTime? HandledAtUtc)
{
    public static ContactMessageResponse From(ContactMessage m) => new(m.Id, m.FullName, m.Email, m.Phone, m.Subject, m.Message, m.Status, m.CreatedAtUtc, m.HandledAtUtc);
}

/// <summary>
/// Self-service brands: a brand owner registers (Pending), the Super Admin approves or rejects, the Brand Admin edits the public profile, and
/// anyone can read the public page of an active brand and send the contact form. Everything is stored in the database.
/// Routes under /api/v1/identity: /auth/register-brand, /tenants/{id}/approve|reject, /brand/profile, /public/brands/{code}, /public/contact, /contact-messages.
/// </summary>
public static partial class BrandSelfServiceEndpoints
{
    public static void MapBrandSelfServiceEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/auth/register-brand", RegisterBrandAsync).WithTags("Brand registration").AllowAnonymous();

        api.MapPost("/tenants/{id:guid}/approve", ApproveAsync).WithTags("Tenants").RequireAuthorization(AicosPolicies.PlatformAdmin);
        api.MapPost("/tenants/{id:guid}/reject", RejectAsync).WithTags("Tenants").RequireAuthorization(AicosPolicies.PlatformAdmin);

        var profile = api.MapGroup("/brand/profile").WithTags("Brand profile").RequireAuthorization(AicosPolicies.AdminOnly);
        profile.MapGet("/", GetProfileAsync);
        profile.MapPut("/", UpdateProfileAsync);

        api.MapGet("/public/brands/{code}", PublicBrandAsync).WithTags("Public").AllowAnonymous();
        api.MapGet("/public/brands", PublicBrandsAsync).WithTags("Public").AllowAnonymous();
        api.MapPost("/public/contact", ContactAsync).WithTags("Public").AllowAnonymous();

        var messages = api.MapGroup("/contact-messages").WithTags("Contact messages").RequireAuthorization(AicosPolicies.PlatformAdmin);
        messages.MapGet("/", ListMessagesAsync);
        messages.MapPost("/{id:guid}/handled", HandledAsync);
    }

    // ------------------------------------------------------------------ registration and review
    private static async Task<IResult> RegisterBrandAsync(
        RegisterBrandRequest r, IdentityOrganizationDbContext db, PasswordService passwords, AuditLogger audit, IEventBus bus, IEmailSender email, ILoggerFactory loggers, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        CommonRules.Required(errors, nameof(r.BrandName), r.BrandName, "Brand name", Tenant.NameMaxLength);
        if (string.IsNullOrWhiteSpace(r.Code) || !CodeRegex().IsMatch(r.Code.Trim().ToLowerInvariant()))
        {
            errors.Add(nameof(r.Code), "Code must be 3-50 characters of letters, digits or '-', starting with a letter.");
        }

        CommonRules.Required(errors, nameof(r.OwnerFullName), r.OwnerFullName, "Owner name", User.FullNameMaxLength);
        CommonRules.Email(errors, nameof(r.Email), r.Email);
        CommonRules.Phone(errors, nameof(r.Phone), r.Phone);
        CommonRules.MaxLength(errors, nameof(r.Description), r.Description, "Description", Tenant.DescriptionMaxLength);
        InputRules.NewPassword(errors, nameof(r.Password), r.Password, nameof(r.ConfirmPassword), r.ConfirmPassword);
        errors.ThrowIfAny();

        var code = Tenant.NormalizeCode(r.Code!);
        if (await db.Tenants.AnyAsync(t => t.Code == code, ct))
        {
            throw new ConflictException($"Brand code '{code}' is already used.", "tenant.duplicate_code");
        }

        await AccountRules.EnsureUniqueAsync(db, r.Email!, username: null, excludeUserId: null, ct);

        var tenant = Tenant.Register(r.BrandName!, code, r.Description);
        var owner = new User(r.OwnerFullName!, r.Email!, username: null, r.Phone, AicosRoles.BrandAdmin, branchId: null, tenant.Id);
        owner.SetPassword(passwords.Hash(owner, r.Password!), mustChangePassword: false);
        db.Tenants.Add(tenant);
        db.Users.Add(owner);
        audit.Record(AuditScope.Platform, tenant.Id, "tenant.registered", "Tenant", tenant.Id, after: AuditSnapshots.Of(tenant));
        await db.SaveChangesAsync(ct);

        await bus.PublishAsync(new BrandRegisteredEvent(tenant.Id, tenant.Name, tenant.Code, owner.Email), ct);
        await SafeMailAsync(email, loggers, EmailTemplates.BrandRegistrationReceived(owner.Email, owner.FullName, tenant.Name), ct);
        return Results.Created($"/api/v1/identity/public/brands/{code}", new RegisterBrandResponse(
            tenant.Id, tenant.Code, tenant.Status, "Registration received. You can sign in once the platform team approves it."));
    }

    private static async Task<TenantResponse> ApproveAsync(
        Guid id, IdentityOrganizationDbContext db, ICurrentUser user, AuditLogger audit, TimeProvider clock, IEmailSender email, IOptions<EmailOptions> mail, ILoggerFactory loggers, CancellationToken ct)
    {
        var tenant = await FindAsync(db, id, ct);
        var before = AuditSnapshots.Of(tenant);
        tenant.Approve(user.UserId, clock.GetUtcNow().UtcDateTime);
        audit.Record(AuditScope.Platform, tenant.Id, "tenant.approved", "Tenant", tenant.Id, before, AuditSnapshots.Of(tenant));
        await db.SaveChangesAsync(ct);
        await NotifyOwnerAsync(db, tenant, email, loggers, owner => EmailTemplates.BrandApproved(owner.Email, owner.FullName, tenant.Name, mail.Value.AppUrl), ct);
        return await TenantResponse.FromAsync(tenant, db, ct);
    }

    private static async Task<TenantResponse> RejectAsync(
        Guid id, RejectTenantRequest r, IdentityOrganizationDbContext db, ICurrentUser user, AuditLogger audit, TimeProvider clock, IEmailSender email, ILoggerFactory loggers, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        CommonRules.Required(errors, nameof(r.Reason), r.Reason, "Reason", Tenant.ReviewNoteMaxLength);
        errors.ThrowIfAny();

        var tenant = await FindAsync(db, id, ct);
        var before = AuditSnapshots.Of(tenant);
        tenant.Reject(user.UserId, r.Reason!, clock.GetUtcNow().UtcDateTime);
        audit.Record(AuditScope.Platform, tenant.Id, "tenant.rejected", "Tenant", tenant.Id, before, AuditSnapshots.Of(tenant));
        await db.SaveChangesAsync(ct);
        await NotifyOwnerAsync(db, tenant, email, loggers, owner => EmailTemplates.BrandRejected(owner.Email, owner.FullName, tenant.Name, tenant.ReviewNote ?? r.Reason!), ct);
        return await TenantResponse.FromAsync(tenant, db, ct);
    }

    private static async Task NotifyOwnerAsync(
        IdentityOrganizationDbContext db, Tenant tenant, IEmailSender email, ILoggerFactory loggers, Func<User, EmailMessage> message, CancellationToken ct)
    {
        var owner = await db.Users.AsNoTracking().Where(u => u.TenantId == tenant.Id && u.Role == AicosRoles.BrandAdmin).OrderBy(u => u.CreatedAtUtc).FirstOrDefaultAsync(ct);
        if (owner is not null)
        {
            await SafeMailAsync(email, loggers, message(owner), ct);
        }
    }

    /// <summary>A mail problem never undoes the decision that was already saved.</summary>
    private static async Task SafeMailAsync(IEmailSender email, ILoggerFactory loggers, EmailMessage message, CancellationToken ct)
    {
        try
        {
            await email.SendAsync(message, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            loggers.CreateLogger("AICOS.Identity.BrandRegistration").LogError("The mail '{Subject}' could not be sent ({Reason}).", message.Subject, ex.GetType().Name);
        }
    }

    // ------------------------------------------------------------------ the profile of the brand
    private static async Task<BrandProfileResponse> GetProfileAsync(IdentityOrganizationDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var tenantId = Tenancy.Require(user);
        return BrandProfileResponse.From(await FindAsync(db, tenantId, ct));
    }

    private static async Task<BrandProfileResponse> UpdateProfileAsync(
        BrandProfileRequest r, IdentityOrganizationDbContext db, ICurrentUser user, AuditLogger audit, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        CommonRules.MaxLength(errors, nameof(r.Description), r.Description, "Description", Tenant.DescriptionMaxLength);
        if (!string.IsNullOrWhiteSpace(r.LogoUrl) && !IsWebUrl(r.LogoUrl.Trim()) && !IsLogoPicture(r.LogoUrl.Trim()))
        {
            errors.Add(nameof(r.LogoUrl), $"The logo must be a link starting with http:// or https:// (at most {Tenant.LogoUrlMaxLength} characters, no user name or password in it).");
        }

        ValidateColor(errors, nameof(r.PrimaryColor), r.PrimaryColor);
        ValidateColor(errors, nameof(r.SecondaryColor), r.SecondaryColor);
        errors.ThrowIfAny();

        var tenant = await FindAsync(db, Tenancy.Require(user), ct);
        var before = BrandProfileResponse.From(tenant);
        tenant.UpdateProfile(r.Description, r.LogoUrl, r.PrimaryColor, r.SecondaryColor);
        audit.Record(AuditScope.Tenant, tenant.Id, "tenant.profile_updated", "Tenant", tenant.Id, before, BrandProfileResponse.From(tenant));
        await db.SaveChangesAsync(ct);
        return BrandProfileResponse.From(tenant);
    }

    private static bool IsLogoPicture(string value) =>
        value.Length <= Tenant.LogoDataMaxLength
        && (value.StartsWith("data:image/png;base64,", StringComparison.Ordinal)
            || value.StartsWith("data:image/jpeg;base64,", StringComparison.Ordinal)
            || value.StartsWith("data:image/webp;base64,", StringComparison.Ordinal)
            || value.StartsWith("data:image/svg+xml;base64,", StringComparison.Ordinal));

    private static bool IsWebUrl(string value) =>
        value.Length <= Tenant.LogoUrlMaxLength
        && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https"
        && string.IsNullOrEmpty(uri.UserInfo)
        && !string.IsNullOrEmpty(uri.Host);

    private static void ValidateColor(ValidationErrors errors, string field, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !ColorRegex().IsMatch(value.Trim()))
        {
            errors.Add(field, "A color is written as #RRGGBB, for example #6F4E37.");
        }
    }

    // ------------------------------------------------------------------ the public page
    /// <summary>Every active brand with at least one active branch: nothing to order from otherwise.</summary>
    private static async Task<IReadOnlyList<PublicBrandCardResponse>> PublicBrandsAsync(IdentityOrganizationDbContext db, CancellationToken ct)
    {
        var branchCounts = await db.Branches.AsNoTracking().Where(b => b.Status == BranchStatus.Active)
            .GroupBy(b => b.TenantId).Select(g => new { TenantId = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.TenantId, x => x.Count, ct);
        var tenants = await db.Tenants.AsNoTracking().Where(t => t.Status == TenantStatus.Active).OrderBy(t => t.Name).ToListAsync(ct);
        return tenants.Where(t => branchCounts.ContainsKey(t.Id))
            .Select(t => new PublicBrandCardResponse(t.Id, t.Code, t.Name, t.Description, t.LogoUrl, t.PrimaryColor, branchCounts[t.Id]))
            .ToList();
    }

    private static async Task<PublicBrandResponse> PublicBrandAsync(string code, IdentityOrganizationDbContext db, CancellationToken ct)
    {
        var normalized = Tenant.NormalizeCode(code);
        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Code == normalized && t.Status == TenantStatus.Active, ct)
            ?? throw new NotFoundException("Brand", code);
        var branches = await db.Branches.AsNoTracking().Where(b => b.TenantId == tenant.Id && b.Status == BranchStatus.Active).OrderBy(b => b.Name).ToListAsync(ct);
        return new PublicBrandResponse(
            tenant.Name, tenant.Code, tenant.Description, tenant.LogoUrl, tenant.PrimaryColor, tenant.SecondaryColor,
            branches.Select(b => new PublicBranchResponse(b.Id, b.Name, b.Address, b.Phone, b.OpeningTime, b.ClosingTime)).ToList());
    }

    // ------------------------------------------------------------------ contact form
    private static async Task<IResult> ContactAsync(ContactRequest r, IdentityOrganizationDbContext db, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        CommonRules.Required(errors, nameof(r.FullName), r.FullName, "Name", ContactMessage.NameMaxLength);
        CommonRules.Email(errors, nameof(r.Email), r.Email);
        CommonRules.Phone(errors, nameof(r.Phone), r.Phone);
        CommonRules.MaxLength(errors, nameof(r.Subject), r.Subject, "Subject", ContactMessage.SubjectMaxLength);
        CommonRules.Required(errors, nameof(r.Message), r.Message, "Message", ContactMessage.MessageMaxLength);
        errors.ThrowIfAny();

        // A bot fills every field of the form, including the hidden one: pretend it worked and keep nothing.
        if (!string.IsNullOrWhiteSpace(r.Website))
        {
            return Results.Accepted(value: new { message = "Thank you, we will get back to you." });
        }

        db.ContactMessages.Add(new ContactMessage(r.FullName!, r.Email!, r.Phone, r.Subject, r.Message!));
        await db.SaveChangesAsync(ct);
        return Results.Accepted(value: new { message = "Thank you, we will get back to you." });
    }

    private static async Task<PagedResponse<ContactMessageResponse>> ListMessagesAsync(
        IdentityOrganizationDbContext db, ContactMessageStatus? status, int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        var query = db.ContactMessages.AsNoTracking();
        if (status is not null)
        {
            query = query.Where(m => m.Status == status);
        }

        return await query.OrderByDescending(m => m.CreatedAtUtc).ToPagedAsync(Math.Max(page, 1), Math.Clamp(pageSize, 1, 100), ContactMessageResponse.From, ct);
    }

    private static async Task<ContactMessageResponse> HandledAsync(Guid id, IdentityOrganizationDbContext db, ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        var message = await db.ContactMessages.FirstOrDefaultAsync(m => m.Id == id, ct) ?? throw new NotFoundException("Contact message", id);
        message.MarkHandled(user.UserId, clock.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct);
        return ContactMessageResponse.From(message);
    }

    private static async Task<Tenant> FindAsync(IdentityOrganizationDbContext db, Guid id, CancellationToken ct) =>
        await db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException("Tenant", id);

    [GeneratedRegex("^[a-z][a-z0-9-]{2,49}$")]
    private static partial Regex CodeRegex();

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex ColorRegex();
}
