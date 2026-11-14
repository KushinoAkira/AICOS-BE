using AICOS.KitchenOperations.Api.Features.Barista;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.KitchenOperations.Api.Domain.Entities;
using AICOS.KitchenOperations.Api.Domain.Enums;
using AICOS.KitchenOperations.Api.Persistence;

namespace AICOS.KitchenOperations.Api.Features;

internal static class KitchenTicketTransitions
{
    public static async Task<(KitchenTicket Ticket, bool Changed)> UpdateAsync(Guid id,
        UpdateKitchenTicketStatusRequest request, KitchenOperationsDbContext db, ICurrentUser user,
        DateTime now, CancellationToken ct, IConfiguration? configuration = null)
    {
        var hint = await TicketAccess.LoadAsync(db, user, id, ct, track: false);
        return await KitchenTicketWrites.RunAsync(db, hint.TenantId, hint.OrderId, async () =>
        {
            var ticket = await TicketAccess.LoadAsync(db, user, id, ct);
            ticket.RequireWholeOrderOperation();
            if (!ticket.IsEligibleForBar && request.Status != KitchenTicketStatus.Cancelled)
                throw new BusinessRuleException("ticket.not_paid", "This order is not eligible for preparation.");

            if (request.Status == KitchenTicketStatus.Cancelled)
            {
                if (!user.IsInRole(AicosRoles.BranchManager) && !user.IsInRole(AicosRoles.BrandAdmin))
                    throw new ForbiddenException("Only a manager cancels a ticket.");
            }
            else if (request.Status is KitchenTicketStatus.Brewing or KitchenTicketStatus.ReadyForPickup or KitchenTicketStatus.Completed)
                KitchenEndpoints.RequireBar(user);
            else
                throw new ValidationFailedException("status", "Status must be Brewing, ReadyForPickup, Completed or Cancelled.");
            ticket.RequireRecoveryClear();

            if (request.ExpectedStatus is not null || request.ExpectedRemakeCount is not null)
            {
                if (request.ExpectedStatus is null || request.ExpectedRemakeCount is null or < 0)
                    throw new ValidationFailedException("expectedStatus", "Supply both expected status and remake count.");
                if (ticket.RemakeCount != request.ExpectedRemakeCount)
                    throw new ConflictException("The preparation cycle changed. Reload the ticket.", "ticket.stale");
                if (ticket.Status == request.Status) return (ticket, false);
                if (ticket.Status != request.ExpectedStatus)
                    throw new ConflictException("The ticket status changed. Reload before confirming.", "ticket.stale");
            }

            if(configuration is not null&&BaristaOperations.Enabled(configuration)&&request.Status==KitchenTicketStatus.Completed)throw new ConflictException("Barista marks READY; staff confirm serving separately.","barista.ready_only");
            if(request.Status!=KitchenTicketStatus.Cancelled)await BaristaOperations.RequireAcceptedAsync(db, ticket, configuration, ct);
            switch (request.Status)
            {
                case KitchenTicketStatus.Brewing: ticket.StartBrewing(now); break;
                case KitchenTicketStatus.ReadyForPickup: ticket.MarkReadyForPickup(now); break;
                case KitchenTicketStatus.Completed: ticket.Complete(now); break;
                case KitchenTicketStatus.Cancelled: ticket.Cancel(now); break;
            }
            await BaristaOperations.CompleteAsync(db,ticket,user.UserId!.Value,now,configuration,ct);
            await db.SaveChangesAsync(ct);
            return (ticket, true);
        }, ct);
    }

    public static async Task<KitchenTicket> RemakeAsync(Guid id, string reason,
        KitchenOperationsDbContext db, ICurrentUser user, DateTime now, CancellationToken ct)
    {
        var hint = await TicketAccess.LoadAsync(db, user, id, ct, track: false);
        return await KitchenTicketWrites.RunAsync(db, hint.TenantId, hint.OrderId, async () =>
        {
            var ticket = await TicketAccess.LoadAsync(db, user, id, ct);
            ticket.RequireWholeOrderOperation();
            ticket.Remake(reason, now);
            await db.SaveChangesAsync(ct);
            return ticket;
        }, ct);
    }
}
