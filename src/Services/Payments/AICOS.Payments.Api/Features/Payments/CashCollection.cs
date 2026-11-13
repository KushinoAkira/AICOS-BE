using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.BuildingBlocks.Validation;
using AICOS.Contracts.Events;
using AICOS.BuildingBlocks.Messaging;
using AICOS.Payments.Api.Domain;
using AICOS.Payments.Api.Infrastructure;
using AICOS.Payments.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.Payments.Api.Features.Payments;

/// <summary>Cash persistence and replay rules. Uses existing Description metadata; no schema change.</summary>
internal static class CashCollection
{
    public static async Task<IResult> RecordAsync(CashPaymentRequest request, HttpContext http, PaymentsDbContext db,
        PaymentProcessor processor, IEventBus bus, ICurrentUser user, IBranchDirectory directory,
        IOrderDirectory orders, ILoggerFactory loggers, CancellationToken ct)
    {
        var tenant = TenantScope.Require(user);
        var branch = await BranchScope.ResolveAsync(user, directory, request.BranchId, requireActive: true, ct);
        var cashier = user.UserId ?? throw new UnauthorizedException("Not signed in.");
        var key = http.Request.Headers["Idempotency-Key"].FirstOrDefault()?.Trim();
        var errors = new ValidationErrors();
        CommonRules.MaxLength(errors, "Idempotency-Key", key, "Request key", 80);
        errors.ThrowIfAny();
        var prefix = string.IsNullOrEmpty(key) ? null : "cash:v1:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{tenant}:{cashier}:{key}"))) + ":";
        var description = prefix is null ? "Cash at counter" : prefix +
            (request.AmountTendered?.ToString("0", CultureInfo.InvariantCulture) ?? "exact");
        Payment? prepared = null;
        var outcome = await PaymentOrderLock.RunAsync(db, tenant, request.OrderId!.Value, async () =>
        {
            db.ChangeTracker.Clear();
            if (prefix is not null && db.Database.IsRelational())
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({prefix}, 0))", ct);
            // Scope by cashier/key before the order check so a lost-response replay still works after Order becomes Paid.
            var replay = prefix is null ? null : await db.Payments.FirstOrDefaultAsync(p => p.TenantId == tenant
                && p.CashierUserId == cashier && p.Method == Payment.CashMethod && p.Description.StartsWith(prefix), ct);
            replay ??= prepared is null ? null : await db.Payments.FirstOrDefaultAsync(p => p.Id == prepared.Id, ct);
            if (replay is not null)
            {
                if (replay.OrderId != request.OrderId || replay.BranchId != branch || replay.Amount != request.Amount
                    || replay.Description != description || replay.Status != PaymentStatus.Succeeded)
                    throw new ConflictException("Request key belongs to a different cash confirmation.", "payment.request_key_conflict");
                return (Payment: replay, Replay: prepared is null);
            }
            await OrderChecks.EnsurePayableAsync(orders, request.OrderId.Value, branch, request.Amount!.Value, ct, db.BillEnabled);
            var existing = await db.Payments.Where(p => p.TenantId == tenant && p.OrderId == request.OrderId).ToListAsync(ct);
            if (existing.Any(p => p.IsPaid))
                throw new ConflictException("This order is already paid.", "payment.already_paid");
            if (existing.Any(p => p.ProviderState == ProviderState.Succeeded && !p.IsPaid))
                throw new ConflictException("Provider reported received money; reconciliation is required.", "payment.reconciliation_required");
            // A transfer could already be on its way: never silently replace a pending online attempt with cash.
            if (existing.Any(p => p.RequiresReconciliation))
                throw new ConflictException("Resolve the pending online payment before taking cash.", "payment.online_pending");
            prepared ??= new Payment(tenant, branch, request.OrderId.Value, request.Amount.Value, Payment.CashMethod, description);
            prepared.RecordCash(cashier, processor.UtcNow);
            db.Payments.Add(prepared);
            await db.SaveChangesAsync(ct);
            return (Payment: prepared, Replay: false);
        }, ct);
        // A replay may repair lost event delivery. Ordering consumers are idempotent for the payment id.
        // Payment rows, not event delivery, are the source of truth for whether cash was recorded.
        try
        {
            await processor.PublishSucceededAsync(outcome.Payment, ct);
            if (!outcome.Replay)
                await bus.PublishAsync(new CashPaymentRecordedEvent(tenant, branch, cashier, outcome.Payment.OrderId,
                    outcome.Payment.Amount, outcome.Payment.PaidAtUtc!.Value), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            loggers.CreateLogger("AICOS.Payments.Cash").LogWarning("Cash committed; notification delivery pending for payment {PaymentId} ({ErrorType}).",
                outcome.Payment.Id, ex.GetType().Name);
        }
        var change = request.AmountTendered is null ? 0 : request.AmountTendered.Value - outcome.Payment.Amount;
        var response = new CashPaymentResponse(PaymentResponse.From(outcome.Payment), change);
        return outcome.Replay ? Results.Ok(response) : Results.Created($"/api/v1/payments/transactions/{outcome.Payment.Id}", response);
    }
}
