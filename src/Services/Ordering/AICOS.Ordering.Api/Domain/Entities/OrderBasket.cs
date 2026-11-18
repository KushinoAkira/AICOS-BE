using AICOS.BuildingBlocks.Domain;

namespace AICOS.Ordering.Api.Domain.Entities;

/// <summary>
/// Denormalised paid basket for Apriori (one row per paid order). ETL target so mining never scans live order rows.
/// </summary>
public sealed class OrderBasket : AuditableEntity<Guid>
{
    private readonly List<OrderBasketItem> _items = [];

    private OrderBasket()
    {
    }

    public OrderBasket(Guid orderId, Guid tenantId, Guid branchId, Guid? customerId, DateTime paidAtUtc, decimal totalAmount)
    {
        Id = orderId;
        TenantId = tenantId;
        BranchId = branchId;
        CustomerId = customerId;
        PaidAtUtc = paidAtUtc;
        TotalAmount = totalAmount;
    }

    public Guid TenantId { get; private set; }

    public Guid BranchId { get; private set; }

    public Guid? CustomerId { get; private set; }

    public DateTime PaidAtUtc { get; private set; }

    public decimal TotalAmount { get; private set; }

    public IReadOnlyCollection<OrderBasketItem> Items => _items.AsReadOnly();

    public void ReplaceItems(IEnumerable<OrderBasketItem> items)
    {
        _items.Clear();
        _items.AddRange(items);
    }
}

public sealed class OrderBasketItem : Entity<Guid>
{
    private OrderBasketItem()
    {
    }

    public OrderBasketItem(Guid basketId, Guid productId, string productName, int quantity, decimal lineAmount)
    {
        Id = Guid.NewGuid();
        BasketId = basketId;
        ProductId = productId;
        ProductName = productName.Trim();
        Quantity = quantity;
        LineAmount = lineAmount;
    }

    public Guid BasketId { get; private set; }

    public Guid ProductId { get; private set; }

    public string ProductName { get; private set; } = string.Empty;

    public int Quantity { get; private set; }

    public decimal LineAmount { get; private set; }
}
