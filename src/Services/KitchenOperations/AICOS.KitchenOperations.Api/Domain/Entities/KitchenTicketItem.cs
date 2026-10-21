namespace AICOS.KitchenOperations.Api.Domain.Entities;

public class KitchenTicketItem
{
    public Guid Id { get; private set; }
    public Guid TicketId { get; private set; }
    public Guid ProductId { get; private set; }
    public string ProductName { get; private set; } = string.Empty;
    public int Quantity { get; private set; }
    public string? Note { get; private set; }

    protected KitchenTicketItem() { }

    public KitchenTicketItem(Guid ticketId, Guid productId, string productName, int quantity, string? note = null)
    {
        if (quantity <= 0)
            throw new ArgumentException("Số lượng món phải lớn hơn 0", nameof(quantity));

        Id = Guid.NewGuid();
        TicketId = ticketId;
        ProductId = productId;
        ProductName = productName;
        Quantity = quantity;
        Note = note;
    }
}
