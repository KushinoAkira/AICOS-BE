using AICOS.BuildingBlocks.Domain;
using AICOS.BuildingBlocks.Exceptions;

namespace AICOS.Ordering.Api.Domain;

/// <summary>A customer's rating of a finished order: one per order, written once.</summary>
public sealed class OrderFeedback : AuditableEntity<Guid>
{
    public const int CommentMaxLength = 500;

    private OrderFeedback()
    {
    }

    public OrderFeedback(Guid tenantId, Guid branchId, Guid orderId, string orderCode, Guid customerId, int rating, string? comment)
    {
        if (rating is < 1 or > 5)
        {
            throw new BusinessRuleException("feedback.invalid_rating", "The rating is between 1 and 5.");
        }

        Id = Guid.NewGuid();
        TenantId = tenantId;
        BranchId = branchId;
        OrderId = orderId;
        OrderCode = orderCode;
        CustomerId = customerId;
        Rating = rating;
        Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
    }

    public Guid TenantId { get; private set; }

    public Guid BranchId { get; private set; }

    public Guid OrderId { get; private set; }

    public string OrderCode { get; private set; } = string.Empty;

    public Guid CustomerId { get; private set; }

    public int Rating { get; private set; }

    public string? Comment { get; private set; }

    /// <summary>The shop's answer to the customer, written by a branch manager or the brand admin. It can be changed later.</summary>
    public string? Reply { get; private set; }

    public DateTime? RepliedAtUtc { get; private set; }

    public Guid? RepliedByUserId { get; private set; }

    public void SetReply(string reply, Guid? userId, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(reply))
        {
            throw new BusinessRuleException("feedback.reply_empty", "Write a reply first.");
        }

        Reply = reply.Trim();
        RepliedAtUtc = nowUtc;
        RepliedByUserId = userId;
    }
}
