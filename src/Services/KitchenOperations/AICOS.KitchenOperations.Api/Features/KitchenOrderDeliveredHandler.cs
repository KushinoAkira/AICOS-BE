using AICOS.BuildingBlocks.Messaging;
using AICOS.Contracts.Events;
using AICOS.KitchenOperations.Api.Domain.Enums;
using AICOS.KitchenOperations.Api.Persistence;
using AICOS.KitchenOperations.Api.Services;

namespace AICOS.KitchenOperations.Api.Features;

/// <summary>An authoritative delivery event completes preparation independently of payment.</summary>
public sealed class KitchenOrderDeliveredHandler(KitchenOperationsDbContext db, IKitchenNotifier notifier,
    ILogger<KitchenOrderDeliveredHandler> logger) : IIntegrationEventHandler<OrderDeliveredIntegrationEvent>
{
    public async Task HandleAsync(OrderDeliveredIntegrationEvent message, CancellationToken ct)
    {
        var ticket = await KitchenTicketWrites.ApplyAsync(db, message.TenantId, message.BranchId, message.OrderId,
            t => t.Status is not (KitchenTicketStatus.Completed or KitchenTicketStatus.Cancelled),
            t => t.ConfirmDelivered(message.DeliveredAtUtc), ct);
        if (ticket is null) return;
        await KitchenEndpoints.SafeAsync(logger, () => notifier.NotifyStatusChangedAsync(
            ticket.TenantId, ticket.BranchId, ticket.Id, ticket.Status.ToString(), ct));
    }
}
