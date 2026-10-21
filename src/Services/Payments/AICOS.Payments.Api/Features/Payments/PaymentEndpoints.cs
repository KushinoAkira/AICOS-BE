using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Security;
using AICOS.BuildingBlocks.Validation;
using AICOS.Contracts.Events;
using AICOS.Payments.Api.Domain;
using AICOS.Payments.Api.Features.Policies;
using AICOS.Payments.Api.Infrastructure;
using AICOS.Payments.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AICOS.Payments.Api.Features.Payments;

/// <summary>POST /transactions: start paying an order online. Staff use their own branch; a customer names the branch of the order.</summary>
public sealed record CreatePaymentRequest(Guid? OrderId, Guid? BranchId, decimal? Amount, string? ProviderCode, string? Description);

/// <summary>POST /transactions/cash: the cashier took cash for an order. <c>AmountTendered</c> is what the customer handed over, to work out the change.</summary>
public sealed record CashPaymentRequest(Guid? OrderId, Guid? BranchId, decimal? Amount, decimal? AmountTendered);

public sealed record CashPaymentResponse(PaymentResponse Payment, decimal Change);

/// <summary>POST /transactions/{id}/simulate: act as the provider. <c>Notify=false</c> loses the webhook on purpose, to try the status query.</summary>
public sealed record SimulatePaymentRequest(string? Outcome, bool? Notify);

/// <summary>
/// Payment service core (SCRUM-85, 86, 87): start an online payment through a provider, take cash, read and cancel a payment, ask the
/// provider for its status, and fake the provider result for demos. Routes: /api/v1/payments/transactions.
/// </summary>
public static class PaymentEndpoints
{
    private const decimal MaxAmount = 100_000_000m;

    public static void MapPaymentEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/transactions").WithTags("Payments");

        group.MapPost("/", CreateAsync).RequireAuthorization();
        group.MapPost("/cash", RecordCashAsync).RequireAuthorization(b => b.RequireRole(PaymentAccess.CounterRoles));
        group.MapGet("/", ListAsync).RequireAuthorization();
        group.MapGet("/{id:guid}", GetAsync).RequireAuthorization();
        group.MapPost("/{id:guid}/cancel", CancelAsync).RequireAuthorization();
        group.MapPost("/{id:guid}/query", QueryAsync).RequireAuthorization();
        group.MapPost("/{id:guid}/simulate", SimulateAsync).RequireAuthorization();
    }

    private static async Task<IResult> CreateAsync(
        CreatePaymentRequest r,
        PaymentsDbContext db,
        PaymentProcessor processor,
        GatewayRegistry gateways,
        SecretProtector protector,
        ICurrentUser user,
        IBranchDirectory directory,
        CancellationToken ct)
    {
        var errors = new ValidationErrors();
        CommonRules.RequiredId(errors, "OrderId", r.OrderId, "Order");
        ValidateAmount(errors, r.Amount);
        CommonRules.MaxLength(errors, "Description", r.Description, "Description", 200);
        var code = PaymentProvider.SupportedCodes.FirstOrDefault(c => string.Equals(c, r.ProviderCode?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (code is null)
        {
            errors.Add("ProviderCode", $"Provider must be one of: {string.Join(", ", PaymentProvider.SupportedCodes)}. Cash is recorded with POST /transactions/cash.");
        }

        errors.ThrowIfAny();

        var tenantId = TenantScope.Require(user);
        var branchId = await ResolveBranchAsync(user, directory, r.BranchId, ct);
        var merchant = await MerchantResolver.ResolveAsync(db, tenantId, branchId, code!, ct)
            ?? throw new BusinessRuleException("payment.method_unavailable", $"{code} is not available at this branch.");

        var orderId = r.OrderId!.Value;
        var amount = r.Amount!.Value;
        var existing = await db.Payments.Where(p => p.TenantId == tenantId && p.OrderId == orderId).ToListAsync(ct);
        foreach (var old in existing.Where(p => p.Status == PaymentStatus.Pending))
        {
            await processor.ExpireIfDueAsync(old, ct);
        }

        if (existing.Any(p => p.IsPaid))
        {
            throw new ConflictException("This order is already paid.", "payment.already_paid");
        }

        var pending = existing.Where(p => p.Status == PaymentStatus.Pending).ToList();
        var same = pending.FirstOrDefault(p => p.Method == code && p.Amount == amount && p.BranchId == branchId && p.CreatedBy == user.UserId);
        if (same is not null)
        {
            return Results.Ok(PaymentResponse.From(same));   // the customer pressed pay twice: hand back the same QR
        }

        foreach (var old in pending)
        {
            old.Cancel("Replaced by a new payment attempt.");
        }

        var policy = await CancellationPolicyEndpoints.ForTenantAsync(db, tenantId, ct);
        var payment = new Payment(tenantId, branchId, orderId, amount, code!, string.IsNullOrWhiteSpace(r.Description) ? "AICOS order" : r.Description.Trim());
        var expiresAt = processor.UtcNow.AddMinutes(policy.UnpaidOrderTimeoutMinutes);
        var setting = merchant.Setting;
        var account = setting?.AccountNumberEncrypted is null ? null : protector.Unprotect(setting.AccountNumberEncrypted);
        var checkout = await gateways.For(code!).CreatePaymentAsync(
            new GatewayPaymentRequest(payment.TransactionRef, amount, payment.Description, expiresAt, setting?.BankCode, account, setting?.AccountName, setting?.MerchantCode), ct);
        payment.SetCheckout(checkout.CheckoutUrl, expiresAt);

        db.Payments.Add(payment);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/payments/transactions/{payment.Id}", PaymentResponse.From(payment));
    }

    private static async Task<IResult> RecordCashAsync(
        CashPaymentRequest r, PaymentsDbContext db, PaymentProcessor processor, IEventBus bus, ICurrentUser user, IBranchDirectory directory, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        CommonRules.RequiredId(errors, "OrderId", r.OrderId, "Order");
        ValidateAmount(errors, r.Amount);
        if (r.AmountTendered is not null && r.Amount is not null && r.AmountTendered < r.Amount)
        {
            errors.Add("AmountTendered", "The cash handed over is less than the amount due.");
        }

        errors.ThrowIfAny();

        var tenantId = TenantScope.Require(user);
        var branchId = await BranchScope.ResolveAsync(user, directory, r.BranchId, requireActive: true, ct);
        var orderId = r.OrderId!.Value;
        var existing = await db.Payments.Where(p => p.TenantId == tenantId && p.OrderId == orderId).ToListAsync(ct);
        if (existing.Any(p => p.IsPaid))
        {
            throw new ConflictException("This order is already paid.", "payment.already_paid");
        }

        foreach (var old in existing.Where(p => p.Status == PaymentStatus.Pending))
        {
            old.Cancel("The customer paid cash instead.");
        }

        var cashier = user.UserId ?? throw new UnauthorizedException("Not signed in.");
        var payment = new Payment(tenantId, branchId, orderId, r.Amount!.Value, Payment.CashMethod, "Cash at counter");
        payment.RecordCash(cashier, processor.UtcNow);
        db.Payments.Add(payment);
        await db.SaveChangesAsync(ct);

        await processor.PublishSucceededAsync(payment, ct);
        await bus.PublishAsync(new CashPaymentRecordedEvent(tenantId, branchId, cashier, orderId, payment.Amount, payment.PaidAtUtc!.Value), ct);

        var change = r.AmountTendered is null ? 0 : r.AmountTendered.Value - payment.Amount;
        return Results.Created($"/api/v1/payments/transactions/{payment.Id}", new CashPaymentResponse(PaymentResponse.From(payment), change));
    }

    private static async Task<List<PaymentResponse>> ListAsync(
        PaymentsDbContext db, ICurrentUser user, Guid? orderId, Guid? branchId, PaymentStatus? status, int? limit, CancellationToken ct)
    {
        var tenantId = TenantScope.Require(user);
        var query = db.Payments.AsNoTracking().Where(p => p.TenantId == tenantId);
        if (PaymentAccess.IsStaff(user))
        {
            var branch = BranchAccess.ResolveFilter(user, branchId);
            if (branch is not null)
            {
                query = query.Where(p => p.BranchId == branch);
            }
        }
        else
        {
            query = query.Where(p => p.CreatedBy == user.UserId);
        }

        if (orderId is not null)
        {
            query = query.Where(p => p.OrderId == orderId);
        }

        if (status is not null)
        {
            query = query.Where(p => p.Status == status);
        }

        var payments = await query.OrderByDescending(p => p.CreatedAtUtc).Take(Math.Clamp(limit ?? 50, 1, 200)).ToListAsync(ct);
        return payments.Select(p => PaymentResponse.From(p)).ToList();
    }

    private static async Task<PaymentResponse> GetAsync(Guid id, PaymentsDbContext db, PaymentProcessor processor, ICurrentUser user, CancellationToken ct)
    {
        var payment = await PaymentAccess.LoadAsync(db, user, id, ct);
        await processor.ExpireIfDueAsync(payment, ct);
        var refunds = await db.PaymentRefunds.AsNoTracking().Where(x => x.PaymentId == id).OrderBy(x => x.CreatedAtUtc).ToListAsync(ct);
        return PaymentResponse.From(payment, refunds.Select(RefundResponse.From).ToList());
    }

    private static async Task<PaymentResponse> CancelAsync(Guid id, PaymentsDbContext db, PaymentProcessor processor, ICurrentUser user, CancellationToken ct)
    {
        var payment = await PaymentAccess.LoadAsync(db, user, id, ct);
        if (await processor.ExpireIfDueAsync(payment, ct) || payment.Status != PaymentStatus.Pending)
        {
            throw new BusinessRuleException("payment.not_pending", $"The payment is {payment.Status} and can no longer be cancelled.");
        }

        payment.Cancel("Cancelled by the customer or staff.");
        await db.SaveChangesAsync(ct);
        await processor.PublishFailedAsync(payment, "Cancelled.", ct);
        return PaymentResponse.From(payment);
    }

    /// <summary>
    /// Transaction status query (SCRUM-87): asks the provider what happened, for a payment whose webhook never came. It also rescues money
    /// that arrived after the payment expired or was cancelled.
    /// </summary>
    private static async Task<PaymentResponse> QueryAsync(
        Guid id, PaymentsDbContext db, PaymentProcessor processor, GatewayRegistry gateways, ICurrentUser user, CancellationToken ct)
    {
        var payment = await PaymentAccess.LoadAsync(db, user, id, ct);
        if (payment.IsCash || payment.IsPaid)
        {
            return PaymentResponse.From(payment);
        }

        var status = await gateways.For(payment.Method).QueryAsync(payment, ct);
        if (status.State == ProviderState.Succeeded)
        {
            await processor.ApplySuccessAsync(payment, status.ProviderReference, ct);
        }
        else if (status.State == ProviderState.Failed && payment.Status == PaymentStatus.Pending)
        {
            await processor.ApplyFailureAsync(payment, status.FailureReason ?? "Declined by the provider.", ct);
        }
        else
        {
            await processor.ExpireIfDueAsync(payment, ct);
        }

        return PaymentResponse.From(payment);
    }

    /// <summary>Plays the part of the provider: the customer "paid" or the payment was declined. Disabled in Production.</summary>
    private static async Task<PaymentResponse> SimulateAsync(
        Guid id,
        SimulatePaymentRequest r,
        PaymentsDbContext db,
        PaymentProcessor processor,
        GatewayRegistry gateways,
        IOptions<PaymentOptions> options,
        IHostEnvironment env,
        ICurrentUser user,
        CancellationToken ct)
    {
        if (!PaymentAccess.AllowSimulation(options, env))
        {
            throw new ForbiddenException("Simulating a provider is disabled in this environment.", "payment.simulation_disabled");
        }

        var success = string.Equals(r.Outcome, "success", StringComparison.OrdinalIgnoreCase);
        if (!success && !string.Equals(r.Outcome, "failure", StringComparison.OrdinalIgnoreCase))
        {
            throw new ValidationFailedException("Outcome", "Outcome must be 'success' or 'failure'.");
        }

        var payment = await PaymentAccess.LoadAsync(db, user, id, ct);
        if (payment.IsCash)
        {
            throw new BusinessRuleException("payment.cash_not_simulated", "Cash payments have no provider to simulate.");
        }

        if (payment.IsPaid)
        {
            throw new BusinessRuleException("payment.already_paid", "The payment is already paid.");
        }

        payment.SetProviderState(success ? ProviderState.Succeeded : ProviderState.Failed);
        if (r.Notify ?? true)
        {
            if (success)
            {
                var status = await gateways.For(payment.Method).QueryAsync(payment, ct);
                await processor.ApplySuccessAsync(payment, status.ProviderReference, ct);
            }
            else
            {
                await processor.ApplyFailureAsync(payment, "Declined by the provider.", ct);
            }
        }
        else
        {
            await db.SaveChangesAsync(ct);
        }

        return PaymentResponse.From(payment);
    }

    private static async Task<Guid> ResolveBranchAsync(ICurrentUser user, IBranchDirectory directory, Guid? requested, CancellationToken ct)
    {
        if (PaymentAccess.IsStaff(user))
        {
            return await BranchScope.ResolveAsync(user, directory, requested, requireActive: true, ct);
        }

        var branchId = requested ?? throw new ValidationFailedException("branchId", "branchId is required.");
        await BranchScope.EnsureUsableAsync(directory, branchId, requireActive: true, ct);
        return branchId;
    }

    private static void ValidateAmount(ValidationErrors errors, decimal? amount)
    {
        CommonRules.Positive(errors, "Amount", amount, "Amount", MaxAmount);
        if (amount is not null && amount != decimal.Truncate(amount.Value))
        {
            errors.Add("Amount", "Amount is in whole VND.");
        }
    }
}
