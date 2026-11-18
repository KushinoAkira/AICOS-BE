using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.Payments.Api.Domain;
using AICOS.Payments.Api.Features.Policies;
using AICOS.Payments.Api.Infrastructure;
using AICOS.Payments.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.Payments.Api.Features.Payments;

public sealed record TakeawayQrRequest(Guid BranchId, decimal Amount);

/// <summary>Staff test-provider admission. Existing payment row is the durable intent; no second ledger.</summary>
public static class TakeawayQrEndpoints
{
    public const string Prefix = "takeaway-qr:v1:";
    public static void MapTakeawayQr(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/staff/takeaway/{orderId:guid}/qr")
            .RequireAuthorization(p => p.RequireRole(PaymentAccess.CounterRoles)).WithTags("Takeaway QR");
        group.MapGet("", ReadAsync);
        group.MapPost("", CreateAsync).AddEndpointFilter<AICOS.Payments.Api.Features.CashierShifts.CashActorFilter>();
    }
    private static bool Enabled(IConfiguration config, IHostEnvironment env, PaymentsDbContext db) =>
        env.IsDevelopment() && config.GetValue<bool>("TakeawayQr:Enabled")
        && config.GetValue<bool>("TakeawayQr:AllowTestProvider") && db.CashierEnabled;
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static object View(Guid orderId, Guid branch, decimal amount, List<Payment> rows, bool available, DateTime now)
    {
        var attempt = rows.OrderByDescending(p => p.CreatedAtUtc).ThenByDescending(p => p.Id).FirstOrDefault();
        var paid = rows.Where(p => p.IsPaid).ToList();
        var invalid = paid.Count > 1 || rows.Any(p => p.BranchId != branch || p.Amount != amount || p.RefundedAmount != 0 || p.Method != "Mock" || !p.Description.StartsWith(Prefix, StringComparison.Ordinal));
        var confirmed = !invalid && paid.Count == 1 && paid[0].Amount == amount && paid[0].PaidAtUtc != null;
        if (confirmed) attempt = paid[0];
        var state = invalid ? "Unknown" : confirmed ? "Confirmed" : attempt is null ? "None"
            : attempt.ProviderState == ProviderState.Failed ? "Failed"
            : attempt.Status == PaymentStatus.Pending && attempt.CheckoutUrl != null && attempt.ExpiresAtUtc > now ? "Pending" : "Unknown";
        return new { Version = 1, OrderId = orderId, BranchId = branch, Amount = amount,
            Available = available, ProviderCode = "Mock", ProviderIsTest = true, State = state,
            CanCreate = available && rows.Count == 0,
            ReasonCode = invalid ? "reconciliation_required" : state == "Failed" ? "provider_failed_review" : null,
            Attempt = attempt == null ? null : PaymentResponse.From(attempt),
            // A Mock token is scannable test data, never a bank transfer instruction.
            QrPayload = available && state == "Pending" && attempt!.Method == "Mock"
                ? "aicos-mock://payment/" + attempt.TransactionRef + "?amount=" + amount.ToString("0", CultureInfo.InvariantCulture) : null };
    }
    private static async Task<object> ReadAsync(Guid orderId, Guid branchId, PaymentsDbContext db,
        ICurrentUser user, IOrderDirectory orders, IConfiguration config, IHostEnvironment env, TimeProvider clock, CancellationToken ct)
    {
        var tenant = TenantScope.Require(user);
        BranchAccess.EnsureCanAccess(user, branchId);
        var order = await orders.GetAsync(orderId, ct);
        if (order.BranchId != branchId || order.IsTableSession)
            throw new ConflictException("Only a takeaway order in this branch is supported.", "payment.takeaway_required");
        var rows = await db.Payments.AsNoTracking().Where(p => p.TenantId == tenant && p.OrderId == orderId).ToListAsync(ct);
        var available = Enabled(config, env, db) && await MerchantResolver.ResolveAsync(db, tenant, branchId, "Mock", ct) != null;
        // No expiry, provider query, notification or SaveChanges on this read.
        return View(orderId, branchId, order.FinalAmount, rows, available, clock.GetUtcNow().UtcDateTime);
    }
    private static async Task<object> CreateAsync(Guid orderId, TakeawayQrRequest r, HttpContext http,
        PaymentsDbContext db, ICurrentUser user, IBranchDirectory branches, IOrderDirectory orders,
        IConfiguration config, IHostEnvironment env, PaymentProcessor processor, GatewayRegistry gateways,
        CancellationToken ct)
    {
        if (!Enabled(config, env, db)) throw new ConflictException("Takeaway test QR is disabled.", "payment.takeaway_qr_disabled");
        var tenant = TenantScope.Require(user);
        var actor = user.UserId ?? throw new UnauthorizedException("Not signed in.");
        var branch = await BranchScope.ResolveAsync(user, branches, r.BranchId, requireActive: true, ct);
        var key = http.Request.Headers["Idempotency-Key"].ToString();
        if (key.Length is < 16 or > 80 || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')
            || r.Amount <= 0 || r.Amount > 100_000_000 || r.Amount != decimal.Truncate(r.Amount))
            throw new BusinessRuleException("payment.invalid_intent", "A stable request key and whole VND amount are required.");
        var prefix = Prefix + Hash($"{tenant}:{actor}:{key}") + ":";
        var description = prefix + Hash($"{orderId}:{branch}:{r.Amount.ToString("0", CultureInfo.InvariantCulture)}:Mock");
        var outcome = await PaymentOrderLock.RunAsync(db, tenant, orderId, async () =>
        {
            db.ChangeTracker.Clear();
            if (db.Database.IsRelational())
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({prefix}, 0))", ct);
            var replay = await db.Payments.FirstOrDefaultAsync(p => p.TenantId == tenant && p.Description.StartsWith(prefix), ct);
            if (replay != null)
            {
                if (replay.Description != description || replay.OrderId != orderId || replay.BranchId != branch || replay.Amount != r.Amount || replay.Method != "Mock" || replay.CreatedBy != actor)
                    throw new ConflictException("Request key belongs to a different intent.", "payment.request_key_conflict");
                return (Payment: replay, Created: false);
            }
            var order = await OrderChecks.EnsurePayableAsync(orders, orderId, branch, r.Amount, ct, db.BillEnabled);
            if (order.IsTableSession) throw new ConflictException("Use the session bill.", "payment.takeaway_required");
            if (await db.Payments.AnyAsync(p => p.TenantId == tenant && p.OrderId == orderId, ct))
                throw new ConflictException("Recover or reconcile the existing attempt first.", "payment.online_pending");
            if (await MerchantResolver.ResolveAsync(db, tenant, branch, "Mock", ct) == null)
                throw new BusinessRuleException("payment.method_unavailable", "Mock provider is not enabled.");
            var payment = new Payment(tenant, branch, orderId, r.Amount, "Mock", description);
            var policy = await CancellationPolicyEndpoints.ForTenantAsync(db, tenant, ct);
            payment.SetCheckout(null, processor.UtcNow.AddMinutes(policy.UnpaidOrderTimeoutMinutes));
            db.Payments.Add(payment);
            await db.SaveChangesAsync(ct);
            return (Payment: payment, Created: true);
        }, ct);
        if (outcome.Created || outcome.Payment.CheckoutUrl == null && !outcome.Payment.IsFinal)
        {
            var checkout = await gateways.For("Mock").CreatePaymentAsync(new GatewayPaymentRequest(
                outcome.Payment.TransactionRef, outcome.Payment.Amount, "Takeaway test QR",
                outcome.Payment.ExpiresAtUtc!.Value, null, null, null, null), ct);
            await PaymentOrderLock.RunAsync(db, tenant, orderId, async () =>
            {
                if (db.Database.IsRelational()) await db.Entry(outcome.Payment).ReloadAsync(ct);
                outcome.Payment.SetCheckout(checkout.CheckoutUrl, outcome.Payment.ExpiresAtUtc);
                await db.SaveChangesAsync(ct);
                return true;
            }, ct);
        }
        var rows = await db.Payments.AsNoTracking().Where(p => p.TenantId == tenant && p.OrderId == orderId).ToListAsync(ct);
        return View(orderId, branch, r.Amount, rows, true, processor.UtcNow);
    }
}