using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Orders;
using AICOS.Contracts.Serving;
using AICOS.KitchenOperations.Api.Domain.Enums;
using AICOS.KitchenOperations.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.KitchenOperations.Api.Features.OrderCancellation;

public static class KitchenCancellationEndpoints
{
    public static void MapKitchenCancellations(this WebApplication app)
    {
        var group = app.MapGroup("/internal/order-cancellations").AllowAnonymous()
            .AddEndpointFilter(async (context, next) => OrderCancellationMachineAuth.Valid(context.HttpContext,
                context.HttpContext.RequestServices.GetRequiredService<IConfiguration>()) ? await next(context) : Results.Unauthorized());
        group.MapPost("/reserve", (CancellationReservation r, KitchenOperationsDbContext db, TimeProvider clock, CancellationToken ct) => Run(r, "reserve", db, clock, ct));
        group.MapPost("/commit", (CancellationReservation r, KitchenOperationsDbContext db, TimeProvider clock, CancellationToken ct) => Run(r, "commit", db, clock, ct));
        group.MapPost("/abort", (CancellationReservation r, KitchenOperationsDbContext db, TimeProvider clock, CancellationToken ct) => Run(r, "abort", db, clock, ct));
    }

    private static Task<CancellationAck> Run(CancellationReservation r, string action, KitchenOperationsDbContext db, TimeProvider clock, CancellationToken ct)
    {
        if (new[] { r.OperationId, r.TenantId, r.BranchId, r.OrderId, r.ActorId }.Any(x => x == Guid.Empty)
            || string.IsNullOrWhiteSpace(r.OrderCode) || r.OrderCode.Length > 40 || string.IsNullOrWhiteSpace(r.TableCode) || r.TableCode.Length > 20
            || r.SourceFingerprint is not { Length: 64 } || r.SourceFingerprint.Any(x => !Uri.IsHexDigit(x)))
            throw new ValidationFailedException("reservation", "Valid cancellation scope and snapshot required.");
        return KitchenTicketWrites.RunAsync(db, r.TenantId, r.OrderId, async () =>
        {
            var hash = CancellationProtocol.Hash(r); var now = clock.GetUtcNow().UtcDateTime;
            var receipt = await db.CancellationReceipts.SingleOrDefaultAsync(x => x.Id == r.OperationId, ct);
            if (receipt is not null && (receipt.RequestHash != hash || receipt.TenantId != r.TenantId || receipt.BranchId != r.BranchId || receipt.OrderId != r.OrderId))
                throw new ConflictException("Cancellation operation scope differs.", "order.cancel_key_conflict");
            if (receipt is not null && receipt.State != "HELD") return receipt.Ack();
            if (action == "abort")
            {
                receipt ??= new() { Id = r.OperationId, TenantId = r.TenantId, BranchId = r.BranchId, OrderId = r.OrderId, RequestHash = hash, CreatedAtUtc = now };
                if (db.Entry(receipt).State == EntityState.Detached) db.CancellationReceipts.Add(receipt);
                receipt.State = "ABORTED"; receipt.CompletedAtUtc = now;
                await db.SaveChangesAsync(ct); return receipt.Ack();
            }
            if (receipt is not null && action == "reserve") return receipt.Ack();
            var ticket = await TicketLookup.ByOrderAsync(db, r.TenantId, r.OrderId, ct);
            if (ticket is null) return new(r.OperationId, r.TenantId, r.BranchId, r.OrderId, hash, "WAITING", "order.cancel_waiting_ticket", null);
            var invalid = !db.BaristaWorkflowEnabled || ticket.BranchId != r.BranchId || ticket.OrderCode != r.OrderCode || ticket.TableNumber != r.TableCode
                || ticket.FulfillmentMode != FulfillmentModes.WholeOrder || ticket.OrderType != "DineIn" || ticket.PayFirst || ticket.IsPaid
                || ticket.Status != KitchenTicketStatus.Pending || ticket.RemakeCount != 0 || ticket.BrewingStartedAtUtc != null
                || ticket.ReadyAtUtc != null || ticket.CompletedAtUtc != null || ticket.RecoveryHoldReason != null
                || await db.BaristaAcceptances.AnyAsync(x => x.TicketId == ticket.Id, ct)
                || await db.PrintJobs.AnyAsync(x => x.TicketId == ticket.Id, ct)
                || await db.CupTasks.AnyAsync(x => x.TicketId == ticket.Id, ct);
            if (receipt is null)
            {
                if (action != "reserve") throw new ConflictException("Reserve before committing cancellation.", "order.cancel_not_reserved");
                receipt = new() { Id = r.OperationId, TenantId = r.TenantId, BranchId = r.BranchId, OrderId = r.OrderId,
                    TicketId = ticket.Id, RequestHash = hash, CreatedAtUtc = now };
                db.CancellationReceipts.Add(receipt);
                if (invalid || await db.CancellationReceipts.AnyAsync(x => x.TenantId == r.TenantId && x.OrderId == r.OrderId && x.State == "HELD", ct))
                { receipt.State = "REJECTED"; receipt.ReasonCode = "order.cancel_bar_review_required"; receipt.CompletedAtUtc = now; }
                await db.SaveChangesAsync(ct); return receipt.Ack();
            }
            if (invalid || receipt.TicketId != ticket.Id) throw new ConflictException("Reserved ticket requires reconciliation.", "order.cancel_reconciliation");
            ticket.Cancel(now); receipt.State = "CANCELLED"; receipt.CompletedAtUtc = now;
            await db.SaveChangesAsync(ct); return receipt.Ack();
        }, ct, cancellationProtocol: true);
    }
}
