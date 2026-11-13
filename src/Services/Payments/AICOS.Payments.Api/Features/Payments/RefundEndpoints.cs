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

namespace AICOS.Payments.Api.Features.Payments;

/// <summary>POST /transactions/{id}/refunds. A missing amount means "as much as the brand policy allows".</summary>
public sealed record RefundRequest(decimal? Amount, RefundKind? Kind, string? Reason);

public sealed record RefundDecisionRequest(string? Note);

/// <summary>POST /refunds/{id}/transferred: the money of a refund awaiting a manual transfer was sent back. <c>Reference</c> is the bank transaction reference.</summary>
public sealed record RefundTransferRequest(string? Reference, string? Note);

/// <summary>
/// Refund request and result (SCRUM-88). A refund inside the brand cancellation policy is paid back at once (when the brand allows
/// automatic approval, BR-75); anything else is an exception that waits for the approver the brand chose (BR-30).
/// Routes: /api/v1/payments/transactions/{id}/refunds and /api/v1/payments/refunds.
/// </summary>
public static class RefundEndpoints
{
    public static void MapRefundEndpoints(this RouteGroupBuilder api)
    {
        // A customer may ask for the refund of their own online payment when they cancel an order: only by the brand policy (Kind Cancellation, no amount).
        api.MapPost("/transactions/{id:guid}/refunds", RequestAsync).WithTags("Refunds").RequireAuthorization(b => b.RequireRole([.. PaymentAccess.CounterRoles, AicosRoles.Customer, AicosRoles.Barista]));

        var group = api.MapGroup("/refunds").WithTags("Refunds");
        group.MapGet("/", ListAsync).RequireAuthorization(b => b.RequireRole(PaymentAccess.CounterRoles));
        group.MapGet("/{id:guid}", GetAsync).RequireAuthorization(b => b.RequireRole(PaymentAccess.CounterRoles));
        group.MapPost("/{id:guid}/approve", ApproveAsync).RequireAuthorization(AicosPolicies.Management);
        group.MapPost("/{id:guid}/reject", RejectAsync).RequireAuthorization(AicosPolicies.Management);
        group.MapPost("/{id:guid}/transferred", TransferredAsync).RequireAuthorization(AicosPolicies.Management);
    }

    private static async Task<IResult> RequestAsync(
        Guid id,
        RefundRequest r,
        PaymentsDbContext db,
        PaymentProcessor processor,
        RefundProcessor refunds,
        ICurrentUser user,
        IEventBus bus,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var errors = new ValidationErrors();
        CommonRules.Required(errors, "Reason", r.Reason, "Reason", 300);
        CommonRules.RequiredEnum(errors, "Kind", r.Kind, "Kind");
        if (r.Amount is not null)
        {
            CommonRules.Positive(errors, "Amount", r.Amount, "Amount");
            if (r.Amount != decimal.Truncate(r.Amount.Value))
            {
                errors.Add("Amount", "Amount is in whole VND.");
            }
        }

        if (r.Kind == RefundKind.Exception && r.Amount is null)
        {
            errors.Add("Amount", "An exception refund needs an amount.");
        }

        errors.ThrowIfAny();

        if (user.IsInRole(AicosRoles.Customer) && (r.Kind != RefundKind.Cancellation || r.Amount is not null))
        {
            throw new ForbiddenException("A customer can only ask for the refund the brand cancellation policy gives.", "refund.customer_policy_only");
        }

        if (user.IsInRole(AicosRoles.Barista) && r.Kind != RefundKind.Unfulfillable)
        {
            throw new ForbiddenException("The bar can only ask for the refund of an order it could not make.", "refund.bar_unfulfillable_only");
        }

        var payment = await PaymentAccess.LoadAsync(db, user, id, ct);
        if (!payment.IsPaid)
        {
            throw new BusinessRuleException("payment.not_refundable", $"Only a paid payment can be refunded (this one is {payment.Status}).");
        }

        var tenantId = payment.TenantId;
        var policy = await CancellationPolicyEndpoints.ForTenantAsync(db, tenantId, ct);
        var pendingTotal = await db.PaymentRefunds
            .Where(x => x.PaymentId == id && (x.Status == RefundStatus.PendingApproval || x.Status == RefundStatus.AwaitingTransfer))
            .SumAsync(x => (decimal?)x.Amount, ct) ?? 0;
        var available = payment.Amount - payment.RefundedAmount - pendingTotal;
        if (available <= 0)
        {
            throw new BusinessRuleException("refund.nothing_to_refund", "Everything of this payment is already refunded or waiting for a decision.");
        }

        var percent = r.Kind switch
        {
            RefundKind.Cancellation => policy.CancelRefundPercent,
            RefundKind.NoShow => policy.NoShowRefundPercent,
            RefundKind.Unfulfillable => 100,
            _ => 0,
        };
        var policyCap = Math.Max(0, decimal.Floor(payment.Amount * percent / 100) - payment.RefundedAmount - pendingTotal);
        var amount = r.Amount ?? Math.Min(policyCap, available);
        if (amount <= 0)
        {
            throw new BusinessRuleException("refund.nothing_to_refund", "The brand policy refunds nothing for this case. Give an amount and use Kind 'Exception' to ask for approval.");
        }

        if (amount > available)
        {
            throw new BusinessRuleException("refund.exceeds_payment", $"At most {available:0} can still be refunded.");
        }

        var within = r.Kind != RefundKind.Exception && amount <= policyCap;
        var refund = new PaymentRefund(payment, amount, r.Kind!.Value, r.Reason!.Trim(), user.UserId, within);
        db.PaymentRefunds.Add(refund);
        await db.SaveChangesAsync(ct);

        var audit = loggers.CreateLogger("AICOS.Payments.Refunds");
        await AuditTrail.PublishAsync(bus, user, audit, tenantId, "refund.requested", "PaymentRefund", refund.Id, null, Snapshot(refund));
        if (within && policy.AutoApproveWithinPolicy)
        {
            await refunds.ProcessAsync(refund, payment, user.UserId, ct);
            await AuditTrail.PublishAsync(bus, user, audit, tenantId, "refund.auto_approved", "PaymentRefund", refund.Id, null, Snapshot(refund));
        }
        else
        {
            await bus.PublishAsync(new RefundRequestedEvent(tenantId, refund.BranchId, refund.Id, payment.OrderId, amount, refund.Reason), ct);
        }

        return Results.Created($"/api/v1/payments/refunds/{refund.Id}", RefundResponse.From(refund));
    }

    private static async Task<List<RefundResponse>> ListAsync(
        PaymentsDbContext db, ICurrentUser user, RefundStatus? status, Guid? branchId, Guid? paymentId, int? limit, CancellationToken ct)
    {
        var tenantId = TenantScope.Require(user);
        var query = db.PaymentRefunds.AsNoTracking().Where(x => x.TenantId == tenantId);
        var branch = BranchAccess.ResolveFilter(user, branchId);
        if (branch is not null)
        {
            query = query.Where(x => x.BranchId == branch);
        }

        if (status is not null)
        {
            query = query.Where(x => x.Status == status);
        }

        if (paymentId is not null)
        {
            query = query.Where(x => x.PaymentId == paymentId);
        }

        var rows = await query.OrderByDescending(x => x.CreatedAtUtc).Take(Math.Clamp(limit ?? 50, 1, 200)).ToListAsync(ct);
        return rows.Select(RefundResponse.From).ToList();
    }

    private static async Task<RefundResponse> GetAsync(Guid id, PaymentsDbContext db, ICurrentUser user, CancellationToken ct) =>
        RefundResponse.From(await LoadAsync(db, user, id, ct));

    private static async Task<RefundResponse> ApproveAsync(
        Guid id, PaymentsDbContext db, RefundProcessor refunds, ICurrentUser user, IEventBus bus, ILoggerFactory loggers, CancellationToken ct)
    {
        var refund = await LoadAsync(db, user, id, ct);
        var policy = await CancellationPolicyEndpoints.ForTenantAsync(db, refund.TenantId, ct);
        if (refund.Status != RefundStatus.PendingApproval)
        {
            throw new BusinessRuleException("refund.not_pending", $"The refund is {refund.Status}.");
        }

        if (policy.ExceptionApprover == RefundApprover.BrandAdmin && !user.IsInRole(AicosRoles.BrandAdmin))
        {
            throw new ForbiddenException("This brand lets only the brand admin approve refund exceptions.", "refund.approver_required");
        }

        if (refund.RequestedBy == user.UserId && !user.IsInRole(AicosRoles.BrandAdmin))
        {
            throw new ForbiddenException("You cannot approve a refund you requested yourself.", "refund.self_approval");
        }

        var payment = await db.Payments.FirstAsync(p => p.Id == refund.PaymentId, ct);
        if (!payment.IsPaid || refund.Amount > payment.Refundable)
        {
            refund.Fail("The payment can no longer cover this refund.", user.UserId, refunds.UtcNow);
            await db.SaveChangesAsync(ct);
            await bus.PublishAsync(new PaymentRefundFailedEvent(refund.TenantId, refund.BranchId, refund.PaymentId, refund.Id, refund.OrderId, refund.DecisionNote ?? "The refund failed."), ct);
            throw new BusinessRuleException("refund.exceeds_payment", "The payment can no longer cover this refund.");
        }

        await refunds.ProcessAsync(refund, payment, user.UserId, ct);
        await AuditTrail.PublishAsync(bus, user, loggers.CreateLogger("AICOS.Payments.Refunds"), refund.TenantId, "refund.approved", "PaymentRefund", refund.Id, null, Snapshot(refund));
        return RefundResponse.From(refund);
    }

    private static async Task<RefundResponse> RejectAsync(
        Guid id, RefundDecisionRequest r, PaymentsDbContext db, RefundProcessor refunds, ICurrentUser user, IEventBus bus, ILoggerFactory loggers, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        CommonRules.Required(errors, "Note", r.Note, "Note", 300);
        errors.ThrowIfAny();

        var refund = await LoadAsync(db, user, id, ct);
        var policy = await CancellationPolicyEndpoints.ForTenantAsync(db, refund.TenantId, ct);
        if (refund.Status != RefundStatus.PendingApproval)
        {
            throw new BusinessRuleException("refund.not_pending", $"The refund is {refund.Status}.");
        }

        if (policy.ExceptionApprover == RefundApprover.BrandAdmin && !user.IsInRole(AicosRoles.BrandAdmin))
        {
            throw new ForbiddenException("This brand lets only the brand admin decide refund exceptions.", "refund.approver_required");
        }

        refund.Reject(r.Note!.Trim(), user.UserId ?? throw new UnauthorizedException("Not signed in."), refunds.UtcNow);
        await db.SaveChangesAsync(ct);
        await bus.PublishAsync(new PaymentRefundFailedEvent(refund.TenantId, refund.BranchId, refund.PaymentId, refund.Id, refund.OrderId, refund.DecisionNote ?? "The refund was rejected."), ct);
        await AuditTrail.PublishAsync(bus, user, loggers.CreateLogger("AICOS.Payments.Refunds"), refund.TenantId, "refund.rejected", "PaymentRefund", refund.Id, null, Snapshot(refund));
        return RefundResponse.From(refund);
    }

    /// <summary>
    /// Staff sent a refund back by hand (payOS has no refund API): the refund is completed with the bank reference, the payment is reduced and
    /// the other services are told, as for any refund. Audited.
    /// </summary>
    private static async Task<RefundResponse> TransferredAsync(
        Guid id, RefundTransferRequest r, PaymentsDbContext db, RefundProcessor refunds, ICurrentUser user, IEventBus bus, ILoggerFactory loggers, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        CommonRules.Required(errors, "Reference", r.Reference, "Bank transaction reference", 80);
        CommonRules.MaxLength(errors, "Note", r.Note, "Note", 300);
        errors.ThrowIfAny();

        var refund = await LoadAsync(db, user, id, ct);
        if (refund.Status != RefundStatus.AwaitingTransfer)
        {
            throw new BusinessRuleException("refund.not_awaiting_transfer", $"The refund is {refund.Status}; only a refund waiting for a manual transfer can be recorded as transferred.");
        }

        var payment = await db.Payments.FirstAsync(p => p.Id == refund.PaymentId, ct);
        if (!payment.IsPaid || refund.Amount > payment.Refundable)
        {
            throw new BusinessRuleException("refund.exceeds_payment", "The payment can no longer cover this refund.");
        }

        var before = Snapshot(refund);
        await refunds.CompleteManualTransferAsync(refund, payment, r.Reference!.Trim(), user.UserId, ct);
        await AuditTrail.PublishAsync(
            bus, user, loggers.CreateLogger("AICOS.Payments.Refunds"), refund.TenantId, "refund.transferred_manually", "PaymentRefund", refund.Id, before,
            new { Refund = Snapshot(refund), Note = string.IsNullOrWhiteSpace(r.Note) ? null : r.Note.Trim() });
        return RefundResponse.From(refund);
    }

    private static async Task<PaymentRefund> LoadAsync(PaymentsDbContext db, ICurrentUser user, Guid id, CancellationToken ct)
    {
        var tenantId = TenantScope.Require(user);
        var refund = await db.PaymentRefunds.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct) ?? throw new NotFoundException("Refund", id);
        BranchAccess.EnsureCanAccess(user, refund.BranchId);
        return refund;
    }

    private static object Snapshot(PaymentRefund r) => new { r.PaymentId, r.OrderId, r.Amount, Kind = r.Kind.ToString(), r.Reason, Status = r.Status.ToString(), r.WithinPolicy, r.DecisionNote };
}

/// <summary>Pays a refund back through the provider (or from the till for cash) and tells the other services.</summary>
public sealed class RefundProcessor(PaymentsDbContext db, GatewayRegistry gateways, IEventBus bus, TimeProvider clock)
{
    public DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    /// <summary>Runs the refund. <paramref name="actor"/> is who approved it (null when the system did, within policy); a cash refund is paid by that person at the till.</summary>
    public async Task ProcessAsync(PaymentRefund refund, Payment payment, Guid? actor, CancellationToken ct)
    {
        string? providerReference = null;
        if (!payment.IsCash)
        {
            var result = await gateways.For(payment.Method).RefundAsync(payment, refund.Amount, refund.Reason, ct);
            if (result.RequiresManualTransfer)
            {
                // Approved, the money goes back by hand: nothing changes on the payment until staff record the transfer.
                refund.AwaitTransfer(result.FailureReason ?? "Transfer the money back by hand.", actor, UtcNow);
                await db.SaveChangesAsync(ct);
                return;
            }

            if (!result.Success)
            {
                refund.Fail(result.FailureReason ?? "The provider refused the refund.", actor, UtcNow);
                await db.SaveChangesAsync(ct);
                await bus.PublishAsync(new PaymentRefundFailedEvent(payment.TenantId, payment.BranchId, payment.Id, refund.Id, payment.OrderId, refund.DecisionNote ?? "The refund failed."), ct);
                return;
            }

            providerReference = result.ProviderReference;
        }

        await CompleteAsync(refund, payment, providerReference, actor, ct);
    }

    /// <summary>A refund that waited for a manual transfer was sent back: <paramref name="bankReference"/> is the bank transaction reference.</summary>
    public Task CompleteManualTransferAsync(PaymentRefund refund, Payment payment, string bankReference, Guid? actor, CancellationToken ct) =>
        CompleteAsync(refund, payment, $"MANUAL:{bankReference}", actor, ct);

    private async Task CompleteAsync(PaymentRefund refund, Payment payment, string? providerReference, Guid? actor, CancellationToken ct)
    {
        refund.Succeed(providerReference, actor, UtcNow);
        payment.ApplyRefund(refund.Amount);
        await db.SaveChangesAsync(ct);

        await bus.PublishAsync(new PaymentRefundedEvent(payment.TenantId, payment.BranchId, payment.Id, refund.Id, payment.OrderId, refund.Amount, payment.RefundedAmount, payment.Method), ct);
        if (payment.IsCash)
        {
            // The till pays money out: negative cash for the closing of whoever hands it back.
            await bus.PublishAsync(new CashPaymentRecordedEvent(payment.TenantId, payment.BranchId, actor ?? payment.CashierUserId ?? Guid.Empty, payment.OrderId, -refund.Amount, UtcNow), ct);
        }
    }
}
