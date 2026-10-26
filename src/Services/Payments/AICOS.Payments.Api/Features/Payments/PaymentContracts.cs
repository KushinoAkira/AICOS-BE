namespace AICOS.Payments.Api.Features.Payments;

public sealed record CreatePaymentRequest(
    Guid RequestId,
    decimal Amount,
    long ExpectedBillVersion);

public sealed record ConfirmBankTransferRequest(
    Guid ConfirmationRequestId,
    long ExpectedVersion,
    string EvidenceReference,
    string? Note);

public sealed record CloseBillRequest(long ExpectedVersion);

public sealed record PaymentResponse(
    Guid Id,
    Guid BillId,
    string Method,
    string Status,
    decimal Amount,
    string? TransactionReference,
    string? EvidenceReference,
    Guid CreatedByUserId,
    Guid? ConfirmedByUserId,
    DateTime? ConfirmedAt,
    DateTime? PaidAt,
    DateTime? CreatedAt,
    long Version,
    string BillStatus,
    long BillVersion);

public sealed record SettlementStatusResponse(
    Guid SessionId,
    Guid? BillId,
    string? BillStatus,
    decimal TotalAmount,
    decimal SucceededAmount,
    decimal OutstandingAmount,
    bool HasPendingPayment,
    bool Settled,
    long Version);
