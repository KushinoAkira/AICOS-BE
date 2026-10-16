namespace AICOS.Contracts.Events;

/// <summary>Published by Inventory Service on any stock movement. Consumed by Reporting and AI Service (forecasting).</summary>
public sealed record StockChangedEvent(
    Guid BranchId,
    Guid IngredientId,
    decimal QuantityDelta,
    decimal QuantityOnHand,
    string Reason) : IntegrationEvent;

/// <summary>Published by Inventory Service when stock falls under the reorder threshold or a batch nears expiry (FEFO).</summary>
public sealed record LowStockAlertEvent(
    Guid BranchId,
    Guid IngredientId,
    decimal QuantityOnHand,
    decimal Threshold) : IntegrationEvent;
