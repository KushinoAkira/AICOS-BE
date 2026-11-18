using AICOS.BuildingBlocks.Messaging;
using AICOS.Contracts.Events;
using AICOS.IdentityOrganization.Api.Domain;
using AICOS.IdentityOrganization.Api.Infrastructure;
using AICOS.IdentityOrganization.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.IdentityOrganization.Api.Features.Receipts;

/// <summary>
/// A member paid an order online (Order Service): the receipt goes to the email address of the member's account, with the brand and branch
/// names kept here. A deleted or locked account, or one without an address, gets nothing. A mail that fails is logged, never retried in a loop:
/// the order is paid whatever happens to the mail.
/// </summary>
public sealed class OrderReceiptHandler(IdentityOrganizationDbContext db, IEmailSender email, ILogger<OrderReceiptHandler> logger)
    : IIntegrationEventHandler<OrderReceiptRequestedIntegrationEvent>
{
    public async Task HandleAsync(OrderReceiptRequestedIntegrationEvent e, CancellationToken cancellationToken)
    {
        var member = await db.Users.AsNoTracking()
            .Where(u => u.Id == e.CustomerId)
            .Select(u => new { u.Email, u.FullName, u.Status })
            .FirstOrDefaultAsync(cancellationToken);
        if (member is null || member.Status != UserStatus.Active || string.IsNullOrWhiteSpace(member.Email))
        {
            logger.LogInformation("No receipt mail for order {OrderCode}: the member account has no usable address.", e.OrderCode);
            return;
        }

        var brand = await db.Tenants.AsNoTracking().Where(t => t.Id == e.TenantId).Select(t => t.Name).FirstOrDefaultAsync(cancellationToken);
        var branch = await db.Branches.AsNoTracking()
            .Where(b => b.Id == e.BranchId)
            .Select(b => new { b.Name, b.Address, b.Phone })
            .FirstOrDefaultAsync(cancellationToken);

        var message = EmailTemplates.OrderReceipt(
            member.Email, member.FullName, brand ?? "AICOS", branch?.Name, branch?.Address, branch?.Phone, e);
        try
        {
            await email.SendAsync(message, cancellationToken);
            logger.LogInformation("Receipt of order {OrderCode} mailed to the member.", e.OrderCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The receipt of order {OrderCode} could not be mailed.", e.OrderCode);
        }
    }
}
