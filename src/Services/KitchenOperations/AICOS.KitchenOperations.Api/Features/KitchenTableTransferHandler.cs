using AICOS.BuildingBlocks.Messaging;
using AICOS.Contracts.Events;
using AICOS.KitchenOperations.Api.Domain.Enums;
using AICOS.KitchenOperations.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.KitchenOperations.Api.Features;

public sealed class KitchenTableTransferHandler(KitchenOperationsDbContext db) : IIntegrationEventHandler<TableTransferredIntegrationEvent>
{
    public async Task HandleAsync(TableTransferredIntegrationEvent message, CancellationToken ct)
    {
        var tickets = await db.Tickets.Where(t => t.TenantId == message.TenantId && t.BranchId == message.BranchId
            && message.OrderIds.Contains(t.OrderId) && t.Status != KitchenTicketStatus.Cancelled && t.Status != KitchenTicketStatus.Completed).ToListAsync(ct);
        foreach (var ticket in tickets) ticket.TransferTable(message.TableCode);
        await db.SaveChangesAsync(ct);
    }
}