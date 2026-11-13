using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.Payments.Api.Domain;
using AICOS.Payments.Api.Features.Policies;
using AICOS.Payments.Api.Infrastructure;
using AICOS.Payments.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.Payments.Api.Features.Payments;

/// <summary>Durable online attempt admission, separate from transport validation and cash collection.</summary>
internal static class OnlinePaymentCreation
{
    public static async Task<IResult> CreateAsync(CreatePaymentRequest r, string code, PaymentsDbContext db,
        PaymentProcessor processor, GatewayRegistry gateways, SecretProtector protector, ICurrentUser user,
        IBranchDirectory directory, IOrderDirectory orders, CancellationToken ct)
    {
        var tenantId = TenantScope.Require(user);
        var outcome = await PaymentOrderLock.RunAsync(db, tenantId, r.OrderId!.Value, async () =>
        {
            db.ChangeTracker.Clear();
            var branchId = await PaymentEndpoints.ResolveBranchAsync(user, directory, r.BranchId, ct);
            await OrderChecks.EnsurePayableAsync(orders, r.OrderId!.Value, branchId, r.Amount!.Value, ct, db.BillEnabled);
            var merchant = await MerchantResolver.ResolveAsync(db, tenantId, branchId, code!, ct)
                ?? throw new BusinessRuleException("payment.method_unavailable", $"{code} is not available at this branch.");

            var orderId = r.OrderId!.Value;
            var amount = r.Amount!.Value;
            var existing = await db.Payments.Where(p => p.TenantId == tenantId && p.OrderId == orderId).ToListAsync(ct);
            if (existing.Any(p => p.IsPaid))
                throw new ConflictException("This order is already paid.", "payment.already_paid");

            var unresolved = existing.Where(p => p.RequiresReconciliation).ToList();
            var same = unresolved.FirstOrDefault(p => p.Status == PaymentStatus.Pending && p.Method == code
                && p.Amount == amount && p.BranchId == branchId && p.CreatedBy == user.UserId);
            if (same is not null && unresolved.Count == 1)
                return (Payment: same, Created: false, Merchant: merchant);
            if (unresolved.Count != 0)
                throw new ConflictException("Reconcile the existing online attempt before starting another payment.", "payment.online_pending");

            var policy = await CancellationPolicyEndpoints.ForTenantAsync(db, tenantId, ct);
            var payment = new Payment(tenantId, branchId, orderId, amount, code!, string.IsNullOrWhiteSpace(r.Description) ? "AICOS order" : r.Description.Trim());
            var expiresAt = processor.UtcNow.AddMinutes(policy.UnpaidOrderTimeoutMinutes);
            payment.SetCheckout(null, expiresAt);
            db.Payments.Add(payment);
            await db.SaveChangesAsync(ct);
            return (Payment: payment, Created: true, Merchant: merchant);
        }, ct);

        if (!outcome.Created) return Results.Ok(PaymentResponse.From(outcome.Payment));

        // Commit the stable reference BEFORE external I/O. An ambiguous timeout leaves
        // a durable pending attempt; retry must not create another provider transaction.
        var setting = outcome.Merchant.Setting;
        var account = setting?.AccountNumberEncrypted is null ? null : protector.Unprotect(setting.AccountNumberEncrypted);
        GatewayCheckout checkout;
        try
        {
            checkout = await gateways.For(code!).CreatePaymentAsync(
                new GatewayPaymentRequest(outcome.Payment.TransactionRef, outcome.Payment.Amount, outcome.Payment.Description,
                    outcome.Payment.ExpiresAtUtc!.Value, setting?.BankCode, account, setting?.AccountName, setting?.MerchantCode), ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new DependencyUnavailableException("payment-provider");
        }

        await PaymentOrderLock.RunAsync(db, tenantId, outcome.Payment.OrderId, async () =>
        {
            if (db.Database.IsRelational()) await db.Entry(outcome.Payment).ReloadAsync(ct);
            // A webhook may have committed while checkout was in flight. Never reset its state.
            outcome.Payment.SetCheckout(checkout.CheckoutUrl, outcome.Payment.ExpiresAtUtc);
            await db.SaveChangesAsync(ct);
            return true;
        }, ct);
        return Results.Created($"/api/v1/payments/transactions/{outcome.Payment.Id}", PaymentResponse.From(outcome.Payment));
    }
}
