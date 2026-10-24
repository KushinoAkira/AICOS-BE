namespace AICOS.Payments.Api.Domain;

public static class BillStatuses
{
    public const string Unpaid = "UNPAID";
    public const string Paid = "PAID";
}

public static class PaymentStatuses
{
    public const string Pending = "PENDING";
    public const string Succeeded = "SUCCEEDED";
}

public static class PaymentMethods
{
    public const string Cash = "CASH";
}

public sealed class Bill
{
    private Bill()
    {
    }

    public Bill(
        Guid id,
        Guid? sessionId,
        Guid? orderId,
        string billCode,
        decimal totalAmount,
        DateTime createdAt)
    {
        Id = id;
        SessionId = sessionId;
        OrderId = orderId;
        BillCode = billCode;
        Status = BillStatuses.Unpaid;
        TotalAmount = totalAmount;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public Guid? SessionId { get; private set; }
    public Guid? OrderId { get; private set; }
    public string BillCode { get; private set; } = string.Empty;
    public string? Status { get; private set; }
    public decimal TotalAmount { get; private set; }
    public DateTime? CreatedAt { get; private set; }
    public ICollection<BillItem> Items { get; private set; } = [];
    public ICollection<Payment> Payments { get; private set; } = [];

    public void MarkPaid()
    {
        if ((Status ?? BillStatuses.Unpaid) != BillStatuses.Unpaid)
        {
            throw new InvalidOperationException("Only an unpaid bill can be paid.");
        }

        Status = BillStatuses.Paid;
    }
}

public sealed class BillItem
{
    private BillItem()
    {
    }

    public BillItem(Guid id, Guid billId, Guid orderItemId, int quantity, decimal amount)
    {
        Id = id;
        BillId = billId;
        OrderItemId = orderItemId;
        Quantity = quantity;
        Amount = amount;
    }

    public Guid Id { get; private set; }
    public Guid BillId { get; private set; }
    public Guid OrderItemId { get; private set; }
    public int Quantity { get; private set; }
    public decimal Amount { get; private set; }
    public Bill Bill { get; private set; } = null!;
}

public sealed class Payment
{
    private Payment()
    {
    }

    public Payment(
        Guid id,
        Guid billId,
        string method,
        decimal amount,
        string? transactionReference,
        DateTime paidAt,
        DateTime createdAt)
    {
        Id = id;
        BillId = billId;
        Method = method;
        Status = PaymentStatuses.Succeeded;
        Amount = amount;
        TransactionReference = transactionReference;
        PaidAt = paidAt;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public Guid BillId { get; private set; }
    public string Method { get; private set; } = string.Empty;
    public string? Status { get; private set; }
    public decimal Amount { get; private set; }
    public string? TransactionReference { get; private set; }
    public DateTime? PaidAt { get; private set; }
    public DateTime? CreatedAt { get; private set; }
    public Bill Bill { get; private set; } = null!;
    public ICollection<Refund> Refunds { get; private set; } = [];
}

public sealed class Refund
{
    private Refund()
    {
    }

    public Guid Id { get; private set; }
    public Guid PaymentId { get; private set; }
    public decimal Amount { get; private set; }
    public string? Reason { get; private set; }
    public string? Status { get; private set; }
    public DateTime? CreatedAt { get; private set; }
    public Payment Payment { get; private set; } = null!;
}
