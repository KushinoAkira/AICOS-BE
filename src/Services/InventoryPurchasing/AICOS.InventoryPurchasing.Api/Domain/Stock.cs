using AICOS.BuildingBlocks.Domain;

namespace AICOS.InventoryPurchasing.Api.Domain;

public enum SupplierStatus
{
    Active,
    Inactive,
}

/// <summary>Supplier of one branch (UC-08).</summary>
public sealed class Supplier : AuditableEntity<Guid>, IBranchScoped
{
    public const int NameMaxLength = 255;

    private Supplier()
    {
    }

    public Supplier(Guid branchId, string name, string? contactName, string? phone, string? email, string? address)
    {
        Id = Guid.NewGuid();
        BranchId = branchId;
        Status = SupplierStatus.Active;
        Update(name, contactName, phone, email, address);
    }

    public Guid BranchId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string? ContactName { get; private set; }

    public string? Phone { get; private set; }

    public string? Email { get; private set; }

    public string? Address { get; private set; }

    public SupplierStatus Status { get; private set; }

    public void Update(string name, string? contactName, string? phone, string? email, string? address)
    {
        Name = name.Trim();
        ContactName = Clean(contactName);
        Phone = Clean(phone);
        Email = Clean(email)?.ToLowerInvariant();
        Address = Clean(address);
    }

    public void ChangeStatus(SupplierStatus status) => Status = status;

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// Ingredient of one branch with its current stock (UC-07). Stock is only changed through <see cref="StockTransaction"/>s
/// (BR-11), never edited directly. <see cref="Version"/> is an optimistic-concurrency token: two simultaneous stock
/// changes on the same ingredient cannot silently overwrite each other (the loser gets 409).
/// </summary>
public sealed class Ingredient : AuditableEntity<Guid>, IBranchScoped
{
    public const int NameMaxLength = 255;

    public static readonly IReadOnlyList<string> AllowedUnits = ["g", "kg", "ml", "l", "pcs", "pack", "bottle", "box", "can"];

    private Ingredient()
    {
    }

    public Ingredient(Guid branchId, string name, string unit, decimal reorderLevel)
    {
        Id = Guid.NewGuid();
        BranchId = branchId;
        Update(name, unit);
        SetReorderLevel(reorderLevel);
    }

    public Guid BranchId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string Unit { get; private set; } = string.Empty;

    /// <summary>Minimum stock threshold (BR-14: never negative). Below it a low-stock alert is raised.</summary>
    public decimal ReorderLevel { get; private set; }

    public decimal QuantityOnHand { get; private set; }

    public int Version { get; private set; }

    public bool IsLowStock => QuantityOnHand < ReorderLevel;

    public void Update(string name, string unit)
    {
        Name = name.Trim();
        Unit = unit.Trim().ToLowerInvariant();
    }

    public void SetReorderLevel(decimal reorderLevel) => ReorderLevel = reorderLevel;

    /// <summary>Only <see cref="Infrastructure.StockLedger"/> calls this.</summary>
    internal void ApplyDelta(decimal delta)
    {
        QuantityOnHand += delta;
        Version++;
    }
}

public enum StockTransactionType
{
    /// <summary>Goods received (manual import or purchase order receipt).</summary>
    Import,

    /// <summary>Goods taken out manually (e.g. transferred, used outside sales).</summary>
    Export,

    /// <summary>Approved physical-count adjustment (BR-15).</summary>
    Adjustment,

    /// <summary>Spoiled / wasted ingredient (UC-35).</summary>
    Waste,

    /// <summary>Consumed by a sale (Order service, later).</summary>
    Sale,
}

/// <summary>One line of the stock ledger. BR-11: always linked to a supporting document number.</summary>
public sealed class StockTransaction : AuditableEntity<Guid>, IBranchScoped
{
    private StockTransaction()
    {
    }

    public StockTransaction(
        Guid branchId, Guid ingredientId, StockTransactionType type, decimal quantityDelta, decimal quantityAfter,
        string documentNo, Guid? referenceId, string? note)
    {
        Id = Guid.NewGuid();
        BranchId = branchId;
        IngredientId = ingredientId;
        Type = type;
        QuantityDelta = quantityDelta;
        QuantityAfter = quantityAfter;
        DocumentNo = documentNo;
        ReferenceId = referenceId;
        Note = note;
    }

    public Guid BranchId { get; private set; }

    public Guid IngredientId { get; private set; }

    public StockTransactionType Type { get; private set; }

    /// <summary>Positive = stock in, negative = stock out.</summary>
    public decimal QuantityDelta { get; private set; }

    public decimal QuantityAfter { get; private set; }

    public string DocumentNo { get; private set; } = string.Empty;

    /// <summary>Purchase order / adjustment / waste report this line comes from.</summary>
    public Guid? ReferenceId { get; private set; }

    public string? Note { get; private set; }
}

/// <summary>How much of an ingredient one unit of a menu product consumes. ProductId lives in Menu Service (no FK).</summary>
public sealed class RecipeItem : Entity<Guid>, IBranchScoped
{
    private RecipeItem()
    {
    }

    public RecipeItem(Guid branchId, Guid productId, Guid ingredientId, decimal quantity)
    {
        Id = Guid.NewGuid();
        BranchId = branchId;
        ProductId = productId;
        IngredientId = ingredientId;
        Quantity = quantity;
    }

    public Guid BranchId { get; private set; }

    public Guid ProductId { get; private set; }

    public Guid IngredientId { get; private set; }

    public decimal Quantity { get; private set; }
}
