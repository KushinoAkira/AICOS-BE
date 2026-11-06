using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.BuildingBlocks.Messaging;
using AICOS.Contracts.Events;
using AICOS.Ordering.Api.Domain;
using AICOS.Ordering.Api.Domain.Enums;
using AICOS.Ordering.Api.Features.Orders;
using AICOS.Ordering.Api.Persistence;
using AICOS.Ordering.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace AICOS.Ordering.Api.Features.Qr;

public sealed record TransferTableRequest(Guid? ExpectedTableId, string? TargetQrToken);
public sealed record TransferTableResponse(Guid SessionId, Guid TableId, string TableCode, int Orders);

public static class TableTransferEndpoints
{
    public static void MapTableTransferEndpoints(this RouteGroupBuilder api) =>
        api.MapPost("/qr/sessions/{id:guid}/transfer", TransferAsync)
            .WithTags("Table Sessions").RequireAuthorization(b => b.RequireRole(OrderAccess.CounterRoles));

    private static async Task<TransferTableResponse> TransferAsync(Guid id, TransferTableRequest request,
        OrderingDbContext db, ICurrentUser user, ITableQrClient tables, IEventBus events, ShopTime clock, CancellationToken ct)
    {
        var tenant = TenantScope.Require(user);
        if (request.ExpectedTableId is null || string.IsNullOrWhiteSpace(request.TargetQrToken) || request.TargetQrToken.Length > 200)
            throw new ValidationFailedException("target", "Expected source table and a target table reference are required.");
        var target = await tables.LookupAsync(request.TargetQrToken, ct) ?? throw new NotFoundException("Target table", request.ExpectedTableId);
        if (target.TenantId != tenant) throw new ForbiddenException("Target belongs to another brand.");
        BranchAccess.EnsureCanAccess(user, target.BranchId);
        if (!target.CanOrder) throw new ConflictException("Target is unavailable.", "table.unavailable");
        var outcome = await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
            // Same lock keys as POS creation; deterministic ordering avoids opposing-move deadlocks.
            foreach (var table in new[] { request.ExpectedTableId.Value, target.TableId }.Distinct().OrderBy(x => x))
            {
                if (transaction is not null)
                {
                    var key = $"aicos:counter-table:{tenant}:{table}";
                    await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);
                }
            }
            var session = await db.TableSessions.FirstOrDefaultAsync(s => s.Id == id && s.TenantId == tenant, ct)
                ?? throw new NotFoundException("Session", id);
            BranchAccess.EnsureCanAccess(user, session.BranchId);
            if (!session.IsOpen) throw new ConflictException("Session is closed.", "qr.session_closed");
            if (session.BranchId != target.BranchId) throw new ForbiddenException("Cannot transfer across branches.");
            if (session.TableId != request.ExpectedTableId && session.TableId != target.TableId)
                throw new ConflictException("Source table changed.", "table.transfer_stale");
            if (await db.TableSessions.AnyAsync(s => s.TenantId == tenant && s.TableId == target.TableId && s.Status == TableSessionStatus.Open && s.Id != id, ct))
                throw new ConflictException("Target already has an open session; use a separate merge workflow.", "table.target_occupied");
            var orders = await db.Orders.Where(o => o.TableSessionId == id
                && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.NoShow && o.Status != OrderStatus.Unfulfillable).ToListAsync(ct);
            if (orders.Any(o => o.PaymentStatus != PaymentStatus.Unpaid || o.Status == OrderStatus.Completed))
                throw new ConflictException("Paid rounds require a separately authorized adjustment.", "table.transfer_paid");
            foreach (var order in orders) order.SwitchServiceType(OrderType.DineIn, target.TableId, target.TableCode, null);
            session.TransferTo(target.TableId, target.TableCode, clock.UtcNow);
            await db.SaveChangesAsync(ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
            return (Response: new TransferTableResponse(session.Id, session.TableId, session.TableCode, orders.Count), Orders: orders.Select(o => o.Id).ToArray());
        });
        await events.PublishAsync(new TableTransferredIntegrationEvent(tenant, target.BranchId, id, target.TableId, target.TableCode, outcome.Orders), ct);
        return outcome.Response;
    }
}