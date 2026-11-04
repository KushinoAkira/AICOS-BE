using System.Data;
using System.Text.Json;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.Ordering.Api.Domain;
using AICOS.Ordering.Api.Domain.Entities;
using AICOS.Ordering.Api.Domain.Enums;
using AICOS.Ordering.Api.Persistence;
using AICOS.Ordering.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AICOS.Ordering.Api.Features.Orders;

/// <summary>POS request replay and table rounds. Uses only Ordering's own tables.</summary>
internal static class CounterOrderRequests
{
    public static async Task<IDbContextTransaction?> LockAsync(OrderingDbContext db, Guid tenant, Guid? cashier, string? key, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(key) || !db.Database.IsRelational()) return null;
        var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            // PostgreSQL transaction lock serializes matching requests across API replicas.
            // Existing nullable-customer unique index alone cannot protect counter orders.
            var name = $"aicos:counter:{tenant}:{cashier}:{key}";
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({name}, 0))", ct);
            return transaction;
        }
        catch { await transaction.DisposeAsync(); throw; }
    }

    public static void EnsureSame(Order order, CreateOrderRequest request, Guid branchId)
    {
        static string Fingerprint(IEnumerable<(Guid Product, int Quantity, string? Note, IEnumerable<Guid> Options)> lines) =>
            JsonSerializer.Serialize(lines.GroupBy(l => JsonSerializer.Serialize(new { l.Product, Note = l.Note?.Trim(), Options = l.Options.OrderBy(x => x).ToArray() }))
                .Select(g => new { Key = g.Key, Quantity = g.Sum(l => l.Quantity) }).OrderBy(l => l.Key).ToArray());
        var actual = Fingerprint(order.Items.Select(i => (i.ProductId, i.Quantity, i.Note, i.Options.Select(o => o.OptionId))));
        var incoming = Fingerprint(request.Items!.Select(i => (i.ProductId!.Value, i.Quantity!.Value, string.IsNullOrWhiteSpace(i.Note) ? null : i.Note.Trim(), (IEnumerable<Guid>)(i.OptionIds ?? []).Distinct())));
        if (order.BranchId != branchId || order.Type != request.Type || order.TableId != request.TableId
            || order.CustomerId != request.CustomerId || order.TableNumber != request.TableNumber
            || order.Note != request.Note?.Trim() || order.DeliveryAddress != request.DeliveryAddress
            || order.PaymentMethod != request.PaymentMethod || order.PromotionCode != request.PromotionCode
            || actual != incoming)
            throw new ConflictException("The request key was already used for a different order.", "order.request_key_conflict");
    }

    public static async Task<TableSession?> ResolveSessionAsync(OrderingDbContext db, ITableQrClient tables,
        Guid tenantId, Guid branchId, CreateOrderRequest request, DateTime now, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.TableQrToken)) return null; // Legacy clients remain compatible.
        if (request.Type != OrderType.DineIn || request.TableId is null)
            throw new ValidationFailedException("TableQrToken", "A table QR reference is only valid for a dine-in order with a table id.");
        var table = await tables.LookupAsync(request.TableQrToken, ct) ?? throw new NotFoundException("Table", request.TableId);
        if (table.TenantId != tenantId || table.BranchId != branchId || table.TableId != request.TableId || table.TableCode != request.TableNumber)
            throw new ForbiddenException("This table does not belong to the order's branch.");
        if (!table.CanOrder) throw new BusinessRuleException("table.unavailable", "This table cannot take orders.");
        if (db.Database.CurrentTransaction is not null)
        {
            var tableLock = $"aicos:counter-table:{tenantId}:{table.TableId}";
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({tableLock}, 0))", ct);
        }
        var session = await db.TableSessions.FirstOrDefaultAsync(s => s.TenantId == tenantId && s.TableId == table.TableId && s.Status == TableSessionStatus.Open, ct);
        if (session is not null) return session;
        session = new TableSession(tenantId, branchId, table.TableId, table.TableCode, now);
        db.TableSessions.Add(session);
        return session;
    }
}
