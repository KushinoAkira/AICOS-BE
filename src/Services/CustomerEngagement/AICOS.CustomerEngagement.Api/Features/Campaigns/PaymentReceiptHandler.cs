using AICOS.BuildingBlocks.Messaging;
using AICOS.Contracts.Events;
using AICOS.CustomerEngagement.Api.Features.Loyalty;
using AICOS.CustomerEngagement.Api.Infrastructure;
using AICOS.CustomerEngagement.Api.Persistence;

namespace AICOS.CustomerEngagement.Api.Features.Campaigns;

/// <summary>Emails a payment confirmation to the member after Ordering publishes OrderPaid.</summary>
public sealed class PaymentReceiptHandler(
    CustomerEngagementDbContext db,
    IEmailSender email,
    ILogger<PaymentReceiptHandler> logger) : IIntegrationEventHandler<OrderPaidIntegrationEvent>
{
    public async Task HandleAsync(OrderPaidIntegrationEvent @event, CancellationToken cancellationToken)
    {
        if (@event.CustomerId is null)
        {
            return;
        }

        var customer = await LoyaltyLookup.CustomerAsync(db, @event.TenantId, @event.CustomerId.Value, cancellationToken);
        if (customer is null || string.IsNullOrWhiteSpace(customer.Email) || customer.Email.Contains("@anonymized.", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            await email.SendAsync(
                CrmEmailTemplates.PaymentReceipt(customer.Email, customer.FullName, @event.OrderCode, @event.Amount, @event.PaidAtUtc),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Payment receipt email failed for order {OrderCode}", @event.OrderCode);
        }
    }
}
