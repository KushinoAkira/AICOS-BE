using AICOS.BuildingBlocks.Domain;

namespace AICOS.Ordering.Api.Domain.Entities;

public class OrderItem : Entity<Guid>
{
    public Guid OrderId { get; private set; }

    public Guid ProductId { get; private set; }

    public string ProductName { get; private set; } = string.Empty;

    public decimal UnitPrice { get; private set; }

    public int Quantity { get; private set; }

    /// <summary>Unit price times quantity. The unit price already includes the surcharge of the chosen options.</summary>
    public decimal TotalPrice => UnitPrice * Quantity;

    private readonly List<OrderLineOption> _options = [];

    public IReadOnlyCollection<OrderLineOption> Options => _options.AsReadOnly();

    public string OptionsText => string.Join(", ", _options.Select(o => o.Name));

    public bool HasSameOptions(IEnumerable<Guid> optionIds) => _options.Select(o => o.OptionId).Order().SequenceEqual(optionIds.Order());

    public string? Note { get; private set; }

    protected OrderItem()
    {
    }

    public OrderItem(Guid orderId, Guid productId, string productName, decimal unitPrice, int quantity, string? note = null, IEnumerable<OrderLineOption>? options = null)
    {
        if (quantity <= 0)
        {
            throw new ArgumentException("Quantity must be greater than 0.", nameof(quantity));
        }

        if (unitPrice < 0)
        {
            throw new ArgumentException("Price cannot be negative.", nameof(unitPrice));
        }

        Id = Guid.NewGuid();
        OrderId = orderId;
        ProductId = productId;
        ProductName = productName;
        UnitPrice = unitPrice;
        Quantity = quantity;
        Note = note;
        if (options is not null)
        {
            _options.AddRange(options);
        }
    }

    public void UpdateQuantity(int newQuantity)
    {
        if (newQuantity <= 0)
        {
            throw new ArgumentException("Quantity must be greater than 0.");
        }

        Quantity = newQuantity;
    }
}
