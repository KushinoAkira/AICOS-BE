namespace AICOS.Ordering.Api.Services.Takeaway;

public static class TakeawayOrderStatuses
{
    public const string PendingPayment = "PENDING_PAYMENT";
    public const string Paid = "PAID";
    public const string Preparing = "PREPARING";
    public const string Ready = "READY";
    public const string PickedUp = "PICKED_UP";
    public const string Cancelled = "CANCELLED";
    public const string NoShow = "NO_SHOW";
}

public static class TakeawayCancelReasons
{
    public const string PaymentTimeout = "PAYMENT_TIMEOUT";
    public const string Customer = "CUSTOMER";
}
