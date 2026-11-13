using System.Text.Json;
using AICOS.Contracts.Billing;
namespace AICOS.Payments.Api.Features.SessionBilling;

public sealed record CollectBillRequest(Guid SessionId, string Fingerprint, decimal? AmountTendered = null, bool ConfirmReceived = false, string? ProviderCode = null);
public sealed record BillAttemptView(Guid Id, string Method, string Status, string TransactionRef, string? CheckoutUrl,
    decimal Amount, decimal? AmountTendered, decimal? Change, DateTime? PaidAtUtc);
public sealed record BillView(Guid Id, Guid SessionId, Guid BranchId, string Status, long Version, string Fingerprint,
    SessionBillQuote Quote, bool NeedsReconciliation, bool OrderingAcknowledged, BillAttemptView? Payment, bool ProviderIsTest = false, bool QrAvailable = false)
{
    public static BillAttemptView View(BillAttempt p) => new(p.Id, p.Method, p.Status, p.TransactionRef, p.CheckoutUrl,
        p.Amount, p.Tendered, p.Tendered is { } t ? t - p.Amount : null, p.PaidAtUtc);
    public static BillView From(SessionBill b, BillAttempt? p, bool testProvider = false) => new(b.Id, b.SessionId, b.BranchId,
        b.Status, b.Version, b.Fingerprint, JsonSerializer.Deserialize<SessionBillQuote>(b.QuoteJson)!, b.NeedsReconciliation,
        b.OrderingAcknowledged, p is null ? null : View(p), testProvider);
}
