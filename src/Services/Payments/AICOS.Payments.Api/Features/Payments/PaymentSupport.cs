using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.Payments.Api.Domain;
using AICOS.Payments.Api.Infrastructure;
using AICOS.Payments.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AICOS.Payments.Api.Features.Payments;

public sealed record PaymentResponse(
    Guid Id,
    Guid BranchId,
    Guid OrderId,
    decimal Amount,
    string Method,
    PaymentStatus Status,
    string TransactionRef,
    string? CheckoutUrl,
    DateTime? ExpiresAtUtc,
    DateTime? PaidAtUtc,
    string? FailureReason,
    bool PaidLate,
    decimal RefundedAmount,
    decimal Refundable,
    DateTime CreatedAtUtc,
    IReadOnlyList<RefundResponse>? Refunds = null,
    Guid? CreatedByUserId = null,
    Guid? CashierUserId = null)
{
    public static PaymentResponse From(Payment p, IReadOnlyList<RefundResponse>? refunds = null) => new(
        p.Id, p.BranchId, p.OrderId, p.Amount, p.Method, p.Status, p.TransactionRef, p.CheckoutUrl, p.ExpiresAtUtc, p.PaidAtUtc, p.FailureReason, p.PaidLate,
        p.RefundedAmount, p.IsPaid ? p.Refundable : 0, p.CreatedAtUtc, refunds, p.CreatedBy, p.CashierUserId);
}

public sealed record RefundResponse(
    Guid Id, Guid PaymentId, Guid BranchId, Guid OrderId, decimal Amount, RefundKind Kind, string Reason, RefundStatus Status, bool WithinPolicy,
    Guid? RequestedBy, Guid? DecidedBy, DateTime? DecidedAtUtc, string? DecisionNote, DateTime CreatedAtUtc)
{
    public static RefundResponse From(PaymentRefund r) => new(
        r.Id, r.PaymentId, r.BranchId, r.OrderId, r.Amount, r.Kind, r.Reason, r.Status, r.WithinPolicy, r.RequestedBy, r.DecidedBy, r.DecidedAtUtc, r.DecisionNote, r.CreatedAtUtc);
}

/// <summary>Who may touch which payment, shared by every endpoint of the Payment service.</summary>
internal static class PaymentAccess
{
    /// <summary>The people who work the counter: they take cash, refund and see every payment of their branch.</summary>
    public static readonly string[] CounterRoles = [AicosRoles.BrandAdmin, AicosRoles.BranchManager, AicosRoles.Cashier];

    public static bool IsStaff(ICurrentUser user) =>
        user.IsInRole(AicosRoles.BrandAdmin) || user.IsInRole(AicosRoles.BranchManager) || user.IsInRole(AicosRoles.Cashier) || user.IsInRole(AicosRoles.Barista);

    /// <summary>
    /// 404 for a payment of another brand, or (for customers and guests) one they did not start; 403 for a payment of another branch of the
    /// brand (staff only).
    /// </summary>
    public static async Task<Payment> LoadAsync(PaymentsDbContext db, ICurrentUser user, Guid id, CancellationToken ct)
    {
        var tenantId = TenantScope.Require(user);
        var payment = await db.Payments.FirstOrDefaultAsync(p => p.Id == id && p.TenantId == tenantId, ct) ?? throw new NotFoundException("Payment", id);
        Ensure(user, payment);
        return payment;
    }

    public static void Ensure(ICurrentUser user, Payment payment)
    {
        if (IsStaff(user))
        {
            BranchAccess.EnsureCanAccess(user, payment.BranchId);
        }
        else if (payment.CreatedBy != user.UserId)
        {
            throw new NotFoundException("Payment", payment.Id);
        }
    }

    public static bool AllowSimulation(IOptions<PaymentOptions> options, IHostEnvironment env) => options.Value.AllowSimulation ?? !env.IsProduction();
}

/// <summary>The provider and receiving account that apply at a branch (UC-20).</summary>
internal sealed record ResolvedMerchant(PaymentProvider Provider, MerchantSetting? Setting);

internal static class MerchantResolver
{
    /// <summary>
    /// The provider if the platform enabled it and the brand did not switch it off at this branch (a branch setting beats the brand one).
    /// A provider nobody set up for the brand is only usable when it is the mock. Null when the method cannot be used.
    /// </summary>
    public static async Task<ResolvedMerchant?> ResolveAsync(PaymentsDbContext db, Guid tenantId, Guid branchId, string providerCode, CancellationToken ct)
    {
        var provider = await db.PaymentProviders.AsNoTracking().FirstOrDefaultAsync(p => p.Code == providerCode && p.IsEnabled, ct);
        if (provider is null)
        {
            return null;
        }

        var settings = await db.MerchantSettings.AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.ProviderCode == providerCode && (s.BranchId == null || s.BranchId == branchId))
            .ToListAsync(ct);
        var effective = settings.FirstOrDefault(s => s.BranchId == branchId) ?? settings.FirstOrDefault(s => s.BranchId == null);
        if (effective is null)
        {
            return provider.IsMock ? new ResolvedMerchant(provider, null) : null;
        }

        return effective.IsEnabled ? new ResolvedMerchant(provider, effective) : null;
    }
}
