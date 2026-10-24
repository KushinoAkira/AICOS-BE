using AICOS.BuildingBlocks.Domain;

namespace AICOS.IdentityOrganization.Api.Domain;

public enum TenantStatus
{
    Active,

    /// <summary>BR-07: every user of a suspended tenant is denied login and API access.</summary>
    Suspended,
}

/// <summary>A brand that rents the platform (UC-05). All brand data hangs off its id (BR-04).</summary>
public sealed class Tenant : AuditableEntity<Guid>
{
    public const int NameMaxLength = 200;
    public const int CodeMaxLength = 50;

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

    public static string NormalizeCode(string code) => code.Trim().ToLowerInvariant();

    public void Update(string name) => Name = name.Trim();

    public void ChangeStatus(TenantStatus status) => Status = status;
}
