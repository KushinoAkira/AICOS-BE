using AICOS.BuildingBlocks.Domain;

namespace AICOS.Payments.Api.Domain;

public enum PaymentStatus
{
    /// <summary>Waiting for the customer to pay through the provider.</summary>
    Pending,
    Succeeded,
    Failed,
    /// <summary>Not paid within the time the brand policy allows (BR-25).</summary>
    Expired,
    Cancelled,
    PartiallyRefunded,
    Refunded,
}

public enum RefundStatus
{
    /// <summary>Outside the brand policy (or auto-approval is off): waits for the approver (BR-30).</summary>
    PendingApproval,
    Succeeded,
    Failed,
    Rejected,
}

/// <summary>Why money goes back; decides which percentage of the brand policy applies.</summary>
public enum RefundKind
{
    /// <summary>The customer cancelled: <c>CancelRefundPercent</c> of the policy.</summary>
    Cancellation,
    /// <summary>Nobody collected the order: <c>NoShowRefundPercent</c> of the policy.</summary>
    NoShow,
    /// <summary>The shop could not make the order (sold out, machine down): the whole payment goes back, whatever the policy says. The bar may ask for it.</summary>
    Unfulfillable,
    /// <summary>Anything else (wrong order, quality complaint...): always an exception that needs the approver.</summary>
    Exception,
}

/// <summary>The state of a payment at the provider's side. Only the simulated gateways keep it here; real ones are asked over HTTP.</summary>
public enum ProviderState
{
    Pending,
    Succeeded,
    Failed,
}

/// <summary>
/// One attempt to pay an order (SCRUM-85/86). The order lives in the Order service, so <see cref="OrderId"/> is only a reference.
/// Method is a provider code (Mock, VietQR, Momo, ZaloPay) or <see cref="CashMethod"/>.
/// </summary>
public sealed class Payment : AuditableEntity<Guid>
{
    public const string CashMethod = "Cash";

    private Payment()
    {
    }

    public Payment(Guid tenantId, Guid branchId, Guid orderId, decimal amount, string method, string description)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        BranchId = branchId;
        OrderId = orderId;
        Amount = amount;
        Method = method;
        Description = description;
        TransactionRef = $"AICOS{Guid.NewGuid():N}"[..20].ToUpperInvariant();
    }

    public Guid TenantId { get; private set; }

    public Guid BranchId { get; private set; }

    public Guid OrderId { get; private set; }

    /// <summary>Whole VND.</summary>
    public decimal Amount { get; private set; }

    public string Method { get; private set; } = string.Empty;

    public PaymentStatus Status { get; private set; }

    public string Description { get; private set; } = string.Empty;

    /// <summary>Our reference sent to the provider (appears in the transfer content); unique.</summary>
    public string TransactionRef { get; private set; } = string.Empty;

    /// <summary>The provider's own transaction id, known once it confirmed.</summary>
    public string? ProviderReference { get; private set; }

    /// <summary>What the customer needs to pay: a QR image / payment link. Null for cash.</summary>
    public string? CheckoutUrl { get; private set; }

    public ProviderState ProviderState { get; private set; }

    public DateTime? ExpiresAtUtc { get; private set; }

    public DateTime? PaidAtUtc { get; private set; }

    public string? FailureReason { get; private set; }

    /// <summary>True when the money arrived after the payment had expired or been cancelled. Money received always wins.</summary>
    public bool PaidLate { get; private set; }

    public decimal RefundedAmount { get; private set; }

    /// <summary>The cashier who took the cash (cash only).</summary>
    public Guid? CashierUserId { get; private set; }

    public bool IsCash => Method == CashMethod;

    public bool IsFinal => Status is not PaymentStatus.Pending;

    public bool IsPaid => Status is PaymentStatus.Succeeded or PaymentStatus.PartiallyRefunded or PaymentStatus.Refunded;

    public void SetCheckout(string? checkoutUrl, DateTime? expiresAtUtc)
    {
        CheckoutUrl = checkoutUrl;
        ExpiresAtUtc = expiresAtUtc;
    }

    public void SetProviderState(ProviderState state) => ProviderState = state;

    public void RecordCash(Guid cashierUserId, DateTime now)
    {
        CashierUserId = cashierUserId;
        ProviderState = ProviderState.Succeeded;
        MarkSucceeded(null, now);
    }

    public void MarkSucceeded(string? providerReference, DateTime now)
    {
        PaidLate = Status is PaymentStatus.Expired or PaymentStatus.Cancelled or PaymentStatus.Failed;
        Status = PaymentStatus.Succeeded;
        ProviderReference = providerReference ?? ProviderReference;
        PaidAtUtc = now;
        FailureReason = null;
    }

    public void MarkFailed(string reason)
    {
        Status = PaymentStatus.Failed;
        FailureReason = reason;
    }

    public void MarkExpired()
    {
        Status = PaymentStatus.Expired;
        FailureReason = "Not paid in time.";
    }

    public void Cancel(string reason)
    {
        Status = PaymentStatus.Cancelled;
        FailureReason = reason;
    }

    public bool IsDue(DateTime now) => Status == PaymentStatus.Pending && ExpiresAtUtc is not null && ExpiresAtUtc <= now;

    public decimal Refundable => Amount - RefundedAmount;

    public void ApplyRefund(decimal amount)
    {
        RefundedAmount += amount;
        Status = RefundedAmount >= Amount ? PaymentStatus.Refunded : PaymentStatus.PartiallyRefunded;
    }
}

/// <summary>A request to give money of a payment back (SCRUM-88).</summary>
public sealed class PaymentRefund : AuditableEntity<Guid>
{
    private PaymentRefund()
    {
    }

    public PaymentRefund(Payment payment, decimal amount, RefundKind kind, string reason, Guid? requestedBy, bool withinPolicy)
    {
        Id = Guid.NewGuid();
        PaymentId = payment.Id;
        TenantId = payment.TenantId;
        BranchId = payment.BranchId;
        OrderId = payment.OrderId;
        Amount = amount;
        Kind = kind;
        Reason = reason;
        RequestedBy = requestedBy;
        WithinPolicy = withinPolicy;
        Status = RefundStatus.PendingApproval;
    }

    public Guid PaymentId { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid BranchId { get; private set; }

    public Guid OrderId { get; private set; }

    public decimal Amount { get; private set; }

    public RefundKind Kind { get; private set; }

    public string Reason { get; private set; } = string.Empty;

    public RefundStatus Status { get; private set; }

    /// <summary>Whether the amount stayed inside the brand policy when it was requested.</summary>
    public bool WithinPolicy { get; private set; }

    public Guid? RequestedBy { get; private set; }

    public Guid? DecidedBy { get; private set; }

    public DateTime? DecidedAtUtc { get; private set; }

    public string? DecisionNote { get; private set; }

    public string? ProviderReference { get; private set; }

    public void Succeed(string? providerReference, Guid? decidedBy, DateTime now)
    {
        Status = RefundStatus.Succeeded;
        ProviderReference = providerReference;
        DecidedBy = decidedBy;
        DecidedAtUtc = now;
    }

    public void Fail(string note, Guid? decidedBy, DateTime now)
    {
        Status = RefundStatus.Failed;
        DecisionNote = note;
        DecidedBy = decidedBy;
        DecidedAtUtc = now;
    }

    public void Reject(string note, Guid decidedBy, DateTime now)
    {
        Status = RefundStatus.Rejected;
        DecisionNote = note;
        DecidedBy = decidedBy;
        DecidedAtUtc = now;
    }
}

/// <summary>One notification received from a provider. The unique (provider, event id) makes a retried webhook harmless (SCRUM-87).</summary>
public sealed class PaymentWebhookEvent : Entity<Guid>
{
    private PaymentWebhookEvent()
    {
    }

    public PaymentWebhookEvent(string providerCode, string eventId, string transactionRef, string reportedStatus, decimal reportedAmount, DateTime now)
    {
        Id = Guid.NewGuid();
        ProviderCode = providerCode;
        EventId = eventId;
        TransactionRef = transactionRef;
        ReportedStatus = reportedStatus;
        ReportedAmount = reportedAmount;
        ReceivedAtUtc = now;
    }

    public string ProviderCode { get; private set; } = string.Empty;

    public string EventId { get; private set; } = string.Empty;

    public string TransactionRef { get; private set; } = string.Empty;

    public string ReportedStatus { get; private set; } = string.Empty;

    public decimal ReportedAmount { get; private set; }

    public Guid? PaymentId { get; private set; }

    public Guid? TenantId { get; private set; }

    /// <summary>applied, applied_late, ignored_final, amount_mismatch or unknown_payment.</summary>
    public string Outcome { get; private set; } = string.Empty;

    public DateTime ReceivedAtUtc { get; private set; }

    public void Resolve(Payment? payment, string outcome)
    {
        PaymentId = payment?.Id;
        TenantId = payment?.TenantId;
        Outcome = outcome;
    }
}
