using System.Security.Cryptography;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Messaging;
using AICOS.Contracts;
using AICOS.Contracts.Events;
using AICOS.InventoryPurchasing.Api.Domain;
using AICOS.InventoryPurchasing.Api.Persistence;

namespace AICOS.InventoryPurchasing.Api.Infrastructure;

/// <summary>
/// The ONLY place stock quantities change. Each change writes a <see cref="StockTransaction"/> (BR-11) and queues
/// StockChangedEvent / LowStockAlertEvent, which are published after the database save succeeded.
/// </summary>
public sealed class StockLedger(InventoryPurchasingDbContext db, IEventBus bus, ILogger<StockLedger> logger)
{
    private readonly List<IntegrationEvent> _pending = [];

    /// <summary>Applies a signed delta. Throws 422 if stock would go negative.</summary>
    public StockTransaction Apply(Ingredient ingredient, StockTransactionType type, decimal delta, string documentNo, Guid? referenceId, string? note)
    {
        var before = ingredient.QuantityOnHand;
        if (before + delta < 0)
        {
            throw new BusinessRuleException(
                "stock.insufficient",
                $"Not enough '{ingredient.Name}' in stock: {before:0.###} {ingredient.Unit} available, {-delta:0.###} {ingredient.Unit} requested.");
        }

        ingredient.ApplyDelta(delta);
        var transaction = new StockTransaction(
            ingredient.BranchId, ingredient.Id, type, delta, ingredient.QuantityOnHand, documentNo, referenceId, note);
        db.StockTransactions.Add(transaction);

        _pending.Add(new StockChangedEvent(ingredient.BranchId, ingredient.Id, delta, ingredient.QuantityOnHand, type.ToString()));

        // NFR-09 real-time alert, raised once when stock crosses below the threshold (not on every later movement).
        if (before >= ingredient.ReorderLevel && ingredient.IsLowStock)
        {
            _pending.Add(new LowStockAlertEvent(ingredient.BranchId, ingredient.Id, ingredient.QuantityOnHand, ingredient.ReorderLevel));
        }

        return transaction;
    }

    /// <summary>Saves and then publishes queued events (Redis is at-most-once: publish failures are only logged).</summary>
    public async Task SaveAndPublishAsync(CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);

        foreach (var @event in _pending)
        {
            try
            {
                await PublishAsync(@event, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not publish {EventType}", @event.GetType().Name);
            }
        }

        _pending.Clear();
    }

    /// <summary>Document numbers such as PO-20261012-4F2A.</summary>
    public static string NewDocumentNo(string prefix, DateTime nowUtc) =>
        $"{prefix}-{nowUtc:yyyyMMdd}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(3))}";

    private Task PublishAsync(IntegrationEvent @event, CancellationToken ct) => @event switch
    {
        StockChangedEvent e => bus.PublishAsync(e, ct),
        LowStockAlertEvent e => bus.PublishAsync(e, ct),
        _ => Task.CompletedTask,
    };
}
