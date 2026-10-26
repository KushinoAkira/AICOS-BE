namespace AICOS.Payments.Api.Domain;

public static class BillStatuses
{
    public const string Unpaid = "UNPAID";
    public const string Paid = "PAID";
    public const string Closed = "CLOSED";
}

public static class BillPrintStatuses
{
    public const string NotIssued = "NOT_ISSUED";
    public const string Printing = "PRINTING";
    public const string Printed = "PRINTED";
    public const string Failed = "FAILED";
}

public static class PaymentStatuses
{
    public const string Pending = "PENDING";
    public const string Succeeded = "SUCCEEDED";
    public const string Failed = "FAILED";
    public const string Cancelled = "CANCELLED";
}

public static class PaymentMethods
{
    public const string Cash = "CASH";
    public const string BankTransfer = "BANK_TRANSFER";
}

public static class PaymentEventTypes
{
    public const string Created = "CREATED";
    public const string Confirmed = "CONFIRMED";
    public const string Failed = "FAILED";
    public const string Cancelled = "CANCELLED";
}

public sealed class Bill
{
    private Bill()
    {
    }

    public Bill(
        Guid id,
        Guid branchId,
        Guid createdByUserId,
        Guid? sessionId,
        Guid? orderId,
        string billCode,
        decimal totalAmount,
        DateTime createdAt)
    {
        Id = id;
        BranchId = branchId;
        CreatedByUserId = createdByUserId;
        SessionId = sessionId;
        OrderId = orderId;
        BillCode = billCode;
        Status = BillStatuses.Unpaid;
        PrintStatus = BillPrintStatuses.NotIssued;
        TotalAmount = totalAmount;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public Guid BranchId { get; private set; }
    public Guid CreatedByUserId { get; private set; }
    public Guid? SessionId { get; private set; }
    public Guid? OrderId { get; private set; }
    public string BillCode { get; private set; } = string.Empty;
    public string? Status { get; private set; }
    public decimal TotalAmount { get; private set; }
    public string PrintStatus { get; private set; } = BillPrintStatuses.NotIssued;
    public DateTime? PrintedAt { get; private set; }
    public Guid? PrintedByUserId { get; private set; }
    public DateTime? ClosedAt { get; private set; }
    public Guid? ClosedByUserId { get; private set; }
    public long Version { get; private set; }
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
        Version++;
    }

    public void RegisterPendingPayment() => Version++;

    public void Close(Guid actorUserId, DateTime closedAt)
    {
        if (Status != BillStatuses.Paid)
        {
            throw new InvalidOperationException("Only a paid bill can be closed.");
        }

        Status = BillStatuses.Closed;
        ClosedByUserId = actorUserId;
        ClosedAt = closedAt;
        Version++;
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

    private Payment(
        Guid id,
        Guid billId,
        Guid createdByUserId,
        string method,
        string status,
        decimal amount,
        string? transactionReference,
        DateTime createdAt)
    {
        Id = id;
        BillId = billId;
        CreatedByUserId = createdByUserId;
        Method = method;
        Status = status;
        Amount = amount;
        TransactionReference = transactionReference;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public Guid BillId { get; private set; }
    public Guid CreatedByUserId { get; private set; }
    public Guid? ConfirmedByUserId { get; private set; }
    public string Method { get; private set; } = string.Empty;
    public string? Status { get; private set; }
    public decimal Amount { get; private set; }
    public string? TransactionReference { get; private set; }
    public string? EvidenceReference { get; private set; }
    public string? StatusReason { get; private set; }
    public DateTime? ConfirmedAt { get; private set; }
    public DateTime? PaidAt { get; private set; }
    public DateTime? CreatedAt { get; private set; }
    public long Version { get; private set; }
    public Bill Bill { get; private set; } = null!;
    public ICollection<PaymentEvent> Events { get; private set; } = [];
    public ICollection<Refund> Refunds { get; private set; } = [];

    public static Payment CreateCash(
        Guid id,
        Guid billId,
        Guid actorUserId,
        decimal amount,
        DateTime now) =>
        new(id, billId, actorUserId, PaymentMethods.Cash, PaymentStatuses.Succeeded, amount, null, now)
        {
            ConfirmedByUserId = actorUserId,
            ConfirmedAt = now,
            PaidAt = now,
        };

    public static Payment CreateBankTransfer(
        Guid id,
        Guid billId,
        Guid actorUserId,
        decimal amount,
        string transactionReference,
        DateTime now) =>
        new(id, billId, actorUserId, PaymentMethods.BankTransfer, PaymentStatuses.Pending, amount, transactionReference, now);

    public void ConfirmBankTransfer(Guid actorUserId, string evidenceReference, DateTime now)
    {
        if (Method != PaymentMethods.BankTransfer || Status != PaymentStatuses.Pending)
        {
            throw new InvalidOperationException("Only a pending bank transfer can be confirmed.");
        }

        Status = PaymentStatuses.Succeeded;
        ConfirmedByUserId = actorUserId;
        ConfirmedAt = now;
        EvidenceReference = evidenceReference;
        PaidAt = now;
        Version++;
    }
}

public sealed class PaymentEvent
{
    private PaymentEvent()
    {
    }

    public PaymentEvent(
        Guid id,
        Guid paymentId,
        Guid requestId,
        string eventType,
        string? fromStatus,
        string toStatus,
        Guid actorUserId,
        string? evidenceReference,
        string? reason,
        string? correlationId,
        DateTime occurredAt)
    {
        Id = id;
        PaymentId = paymentId;
        RequestId = requestId;
        EventType = eventType;
        FromStatus = fromStatus;
        ToStatus = toStatus;
        ActorUserId = actorUserId;
        EvidenceReference = evidenceReference;
        Reason = reason;
        CorrelationId = correlationId;
        OccurredAt = occurredAt;
    }

    public Guid Id { get; private set; }
    public Guid PaymentId { get; private set; }
    public Guid RequestId { get; private set; }
    public string EventType { get; private set; } = string.Empty;
    public string? FromStatus { get; private set; }
    public string ToStatus { get; private set; } = string.Empty;
    public Guid ActorUserId { get; private set; }
    public string? EvidenceReference { get; private set; }
    public string? Reason { get; private set; }
    public string? CorrelationId { get; private set; }
    public DateTime OccurredAt { get; private set; }
    public Payment Payment { get; private set; } = null!;
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
