using AICOS.BuildingBlocks.Domain;

namespace AICOS.CustomerEngagement.Api.Domain;

public enum CrmCampaignType
{
    Birthday,
    WinBack,
}

/// <summary>Idempotency log for CRM emails (one Birthday per year, one WinBack per cool-down window).</summary>
public sealed class CrmCampaignSend : AuditableEntity<Guid>
{
    private CrmCampaignSend()
    {
    }

    public CrmCampaignSend(Guid tenantId, Guid customerId, CrmCampaignType type, DateOnly sentOn, string? voucherCode)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        CustomerId = customerId;
        Type = type;
        SentOn = sentOn;
        VoucherCode = voucherCode;
    }

    public Guid TenantId { get; private set; }

    public Guid CustomerId { get; private set; }

    public CrmCampaignType Type { get; private set; }

    public DateOnly SentOn { get; private set; }

    public string? VoucherCode { get; private set; }
}
