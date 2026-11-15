using AICOS.BuildingBlocks.Messaging;
using AICOS.Contracts.Events;
using AICOS.KitchenOperations.Api.Domain.Enums;
using AICOS.KitchenOperations.Api.Persistence;

namespace AICOS.KitchenOperations.Api.Features;

public sealed class KitchenTableTransferHandler(KitchenOperationsDbContext db, IConfiguration? config = null) : IIntegrationEventHandler<TableTransferredIntegrationEvent>
{
    public async Task HandleAsync(TableTransferredIntegrationEvent message, CancellationToken ct)
    {
        if(config?.GetValue<bool>("TableTransfer:Enabled")==true)return; // Versioned HTTP routing owns transfers while enabled.
        foreach (var orderId in message.OrderIds.Distinct().Order())
            await KitchenTicketWrites.ApplyAsync(db, message.TenantId, message.BranchId, orderId,
                ticket => ticket.Status is not (KitchenTicketStatus.Cancelled or KitchenTicketStatus.Completed),
                ticket => ticket.TransferTable(message.TableCode), ct);
    }
}
