using AICOS.BuildingBlocks.Domain;

namespace AICOS.IdentityOrganization.Api.Domain;

public enum BranchStatus
{
    Active,

    /// <summary>BR-38: a suspended branch loses system access (no login, no orders) but its data is kept.</summary>
    Suspended,
}

/// <summary>A coffee shop branch of a brand (UC-12). Belongs to exactly one tenant (BR-04).</summary>
public sealed class Branch : AuditableEntity<Guid>
{
    public const int NameMaxLength = 255;
    public const int AddressMaxLength = 500;
    public const int PhoneMaxLength = 20;

    private Branch()
    {
    }

    public Branch(Guid tenantId, string name, string? address, string? phone, TimeOnly? openingTime, TimeOnly? closingTime)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        Status = BranchStatus.Active;
        Update(name, address, phone, openingTime, closingTime);
    }

    public Guid TenantId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string? Address { get; private set; }

    public string? Phone { get; private set; }

    public TimeOnly? OpeningTime { get; private set; }

    public TimeOnly? ClosingTime { get; private set; }

    public BranchStatus Status { get; private set; }

    public void Update(string name, string? address, string? phone, TimeOnly? openingTime, TimeOnly? closingTime)
    {
        Name = name.Trim();
        Address = string.IsNullOrWhiteSpace(address) ? null : address.Trim();
        Phone = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim();
        OpeningTime = openingTime;
        ClosingTime = closingTime;
    }

    public void ChangeStatus(BranchStatus status) => Status = status;
}
