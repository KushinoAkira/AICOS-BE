using AICOS.BuildingBlocks.Domain;

namespace AICOS.CustomerEngagement.Api.Domain;

/// <summary>
/// A message for a member about one of their orders (paid, ready, remade, cancelled...). It belongs to the customer account (<see cref="UserId"/>)
/// of a brand. <see cref="DedupeKey"/> keeps a repeated event from creating a second notification of the same kind for the same order.
/// </summary>
public sealed class CustomerNotification : AuditableEntity<Guid>
{
    private CustomerNotification()
    {
    }

    public CustomerNotification(Guid tenantId, Guid userId, Guid orderId, string orderCode, string kind, string dedupeKey, string title, string body)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        UserId = userId;
        OrderId = orderId;
        OrderCode = orderCode;
        Kind = kind;
        DedupeKey = dedupeKey;
        Title = title;
        Body = body;
    }

    public Guid TenantId { get; private set; }

    public Guid UserId { get; private set; }

    public Guid OrderId { get; private set; }

    public string OrderCode { get; private set; } = string.Empty;

    public string Kind { get; private set; } = string.Empty;

    public string DedupeKey { get; private set; } = string.Empty;

    public string Title { get; private set; } = string.Empty;

    public string Body { get; private set; } = string.Empty;

    public DateTime? ReadAtUtc { get; private set; }

    public bool MarkRead(DateTime now)
    {
        if (ReadAtUtc is not null)
        {
            return false;
        }

        ReadAtUtc = now;
        return true;
    }
}
