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

    /// <summary>Created or approved, but its plan is not paid yet: nobody of the brand can sign in until the Super Admin records the payment.</summary>
    AwaitingPayment,

    /// <summary>Removed by the Super Admin (soft delete): hidden from lists, nobody can sign in, the data and the code are kept.</summary>
    Deleted,
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

    /// <summary>The plan the owner picked (and paid, they say) on the pricing page when registering; assigned when the registration is approved.</summary>
    public Guid? RequestedPlanId { get; private set; }

    /// <summary>The payment reference the owner got from the (simulated) checkout; the Super Admin checks it before recording the payment.</summary>
    public string? PaymentClaimReference { get; private set; }

    public DateTime? DeletedAtUtc { get; private set; }

    public string? DeleteReason { get; private set; }

    public void RequestPlan(Guid? planId, string? paymentReference)
    {
        RequestedPlanId = planId;
        PaymentClaimReference = string.IsNullOrWhiteSpace(paymentReference) ? null : paymentReference.Trim();
    }

    /// <summary>A new brand waits for its first payment before anyone can use it.</summary>
    public void AwaitPayment() => Status = TenantStatus.AwaitingPayment;

    /// <summary>The first payment arrived: the brand opens.</summary>
    public void ActivateAfterPayment()
    {
        if (Status == TenantStatus.AwaitingPayment)
        {
            Status = TenantStatus.Active;
        }
    }

    public void SoftDelete(string? reason, DateTime now)
    {
        if (Status == TenantStatus.Deleted)
        {
            throw new ConflictException("This brand is already deleted.", "tenant.deleted");
        }

        Status = TenantStatus.Deleted;
        DeletedAtUtc = now;
        DeleteReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
    }

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

    /// <summary>Accepts the registration. The brand then waits for its payment (<see cref="TenantStatus.AwaitingPayment"/>).</summary>
    public void Approve(Guid? reviewer, DateTime now)
    {
        EnsurePending();
        Status = TenantStatus.AwaitingPayment;
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
