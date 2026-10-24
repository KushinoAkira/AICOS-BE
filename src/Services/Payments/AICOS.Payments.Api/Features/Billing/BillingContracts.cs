namespace AICOS.Payments.Api.Features.Billing;

public sealed record CreateBillRequest(
    Guid RequestId,
    Guid? SessionId,
    Guid? OrderId);

public sealed record BillResponse(
    Guid Id,
    string BillCode,
    string Status,
    Guid? SessionId,
    Guid? OrderId,
    decimal TotalAmount,
    DateTime? CreatedAt,
    IReadOnlyList<BillItemResponse> Items,
    IReadOnlyList<BillPaymentResponse> Payments);

public sealed record BillItemResponse(
    Guid Id,
    Guid OrderItemId,
    int Quantity,
    decimal Amount);

public sealed record BillPaymentResponse(
    Guid Id,
    string Method,
    string Status,
    decimal Amount,
    string? TransactionReference,
    DateTime? PaidAt);
