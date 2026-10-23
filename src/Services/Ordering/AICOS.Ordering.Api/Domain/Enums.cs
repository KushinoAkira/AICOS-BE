namespace AICOS.Ordering.Api.Domain.Enums;

public enum OrderStatus
{
    Pending = 1,        // created, not yet confirmed
    Confirmed = 2,      // confirmed (paid, or dine-in sent to the bar)
    Preparing = 3,      // the barista is making it
    Ready = 4,          // made, waiting to be served
    Completed = 5,      // the customer has it
    Cancelled = 6,
}

public enum OrderType
{
    DineIn = 1,
    Takeaway = 2,
    Delivery = 3,
}

public enum PaymentStatus
{
    Unpaid = 1,
    PartiallyPaid = 2,
    Paid = 3,
    Refunded = 4,
}

public enum PaymentMethod
{
    Cash = 1,
    Card = 2,
    EWallet = 3,
    BankTransfer = 4,
}

/// <summary>Where the money of an order that was cancelled after it was paid stands (FR-30).</summary>
public enum RefundStatus
{
    None = 0,
    /// <summary>A paid order was cancelled and nobody has asked for the money back yet (for example the payment arrived after the cancellation).</summary>
    Needed = 1,
    RefundPending = 2,
    Refunded = 3,
    RefundFailed = 4,
}
