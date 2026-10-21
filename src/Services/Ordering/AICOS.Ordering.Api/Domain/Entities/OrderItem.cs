namespace AICOS.Ordering.Api.Domain.Entities;

public class OrderItem
{
    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid ProductId { get; private set; }
    public string ProductName { get; private set; } = string.Empty;
    public decimal UnitPrice { get; private set; }
    public int Quantity { get; private set; }
    public decimal TotalPrice => UnitPrice * Quantity;
    public string? Note { get; private set; }

    // Constructor phục vụ cho Entity Framework
    protected OrderItem() { }

    public OrderItem(Guid orderId, Guid productId, string productName, decimal unitPrice, int quantity, string? note = null)
    {
        if (quantity <= 0)
            throw new ArgumentException("Số lượng phải lớn hơn 0", nameof(quantity));
        if (unitPrice < 0)
            throw new ArgumentException("Giá không được âm", nameof(unitPrice));

        Id = Guid.NewGuid();
        OrderId = orderId;
        ProductId = productId;
        ProductName = productName;
        UnitPrice = unitPrice;
        Quantity = quantity;
        Note = note;
    }

    // Các hàm cập nhật nghiệp vụ
    public void UpdateQuantity(int newQuantity)
    {
        if (newQuantity <= 0)
            throw new ArgumentException("Số lượng phải lớn hơn 0");
        
        Quantity = newQuantity;
    }
}
