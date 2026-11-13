using AICOS.BuildingBlocks.Domain;
using AICOS.BuildingBlocks.Exceptions;

namespace AICOS.IdentityOrganization.Api.Domain;

public enum TenantStatus
{
    Active,

    /// <summary>BR-07: every user of a suspended tenant is denied login and API access.</summary>
    Suspended,

    /// <summary>Registered by the brand owner and waiting for a Super Admin decision. Nobody of the brand can sign in yet.</summary>
    Pending,

    /// <summary>The registration was refused. The brand never became active.</summary>
    Rejected,
}

/// <summary>A brand that rents the platform (UC-05). All brand data hangs off its id (BR-04).</summary>
public sealed class Tenant : AuditableEntity<Guid>
{
    public const int NameMaxLength = 200;
    public const int CodeMaxLength = 50;
    public const int DescriptionMaxLength = 500;
    public const int LogoUrlMaxLength = 500;

    /// <summary>A logo picture sent as a data URL; the web app shrinks it to at most 256 px first.</summary>
    public const int LogoDataMaxLength = 200_000;
    public const int ReviewNoteMaxLength = 300;

    private Tenant()
    {
    }

    public Tenant(string name, string code)
    {
        Id = Guid.NewGuid();
        Status = TenantStatus.Active;
        Update(name);
        Code = NormalizeCode(code);
    }

    public string Name { get; private set; } = string.Empty;

    /// <summary>Tenant identifier / subdomain, lower-case, unique (FR-01).</summary>
    public string Code { get; private set; } = string.Empty;

    public TenantStatus Status { get; private set; }

    /// <summary>What the public page of the brand says about it.</summary>
    public string? Description { get; private set; }

    /// <summary>Address of the logo image (the front end stores the file and sends the link); shown by the public brand page.</summary>
    public string? LogoUrl { get; private set; }

    /// <summary>Brand colors as <c>#RRGGBB</c>.</summary>
    public string? PrimaryColor { get; private set; }

    public string? SecondaryColor { get; private set; }

    /// <summary>Who decided on a self-service registration, when, and the reason given for a refusal.</summary>
    public Guid? ReviewedBy { get; private set; }

    public DateTime? ReviewedAtUtc { get; private set; }

    public string? ReviewNote { get; private set; }

    public static string NormalizeCode(string code) => code.Trim().ToLowerInvariant();

    /// <summary>A brand registered by its owner: it does nothing until a Super Admin approves it.</summary>
    public static Tenant Register(string name, string code, string? description)
    {
        var tenant = new Tenant(name, code)
        {
            Status = TenantStatus.Pending,
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
        };
        return tenant;
    }

    public void Update(string name) => Name = name.Trim();

    public void ChangeStatus(TenantStatus status) => Status = status;

    public void Approve(Guid? reviewer, DateTime now)
    {
        EnsurePending();
        Status = TenantStatus.Active;
        ReviewedBy = reviewer;
        ReviewedAtUtc = now;
        ReviewNote = null;
    }

    public void Reject(Guid? reviewer, string reason, DateTime now)
    {
        EnsurePending();
        Status = TenantStatus.Rejected;
        ReviewedBy = reviewer;
        ReviewedAtUtc = now;
        ReviewNote = reason.Trim();
    }

    /// <summary>The display name shown to customers and staff. The code (subdomain, links, QR codes) stays the same.</summary>
    public void Rename(string name) => Name = name.Trim();

    public void UpdateProfile(string? description, string? logoUrl, string? primaryColor, string? secondaryColor)
    {
        Description = Clean(description);
        LogoUrl = Clean(logoUrl);
        PrimaryColor = Clean(primaryColor)?.ToUpperInvariant();
        SecondaryColor = Clean(secondaryColor)?.ToUpperInvariant();
    }

    private void EnsurePending()
    {
        if (Status != TenantStatus.Pending)
        {
            throw new ConflictException($"Only a pending registration can be decided (this brand is {Status}).", "tenant.not_pending");
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
