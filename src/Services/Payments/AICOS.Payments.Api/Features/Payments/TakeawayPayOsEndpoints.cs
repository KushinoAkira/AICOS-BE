using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.Payments.Api.Domain;
using AICOS.Payments.Api.Infrastructure;
using AICOS.Payments.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AICOS.Payments.Api.Features.Payments;

public static class TakeawayPayOsEndpoints
{
    public static void MapTakeawayPayOs(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/staff/takeaway/{orderId:guid}/payos").RequireAuthorization(p => p.RequireRole(PaymentAccess.CounterRoles));
        group.MapGet("", ReadAsync);
        group.MapPost("", CreateAsync).AddEndpointFilter<Features.CashierShifts.CashActorFilter>();
        group.MapPost("/{paymentId:guid}/query", QueryAsync).AddEndpointFilter<Features.CashierShifts.CashActorFilter>();
    }
    private static async Task<object> ReadAsync(Guid orderId, Guid branchId, PaymentsDbContext db, ICurrentUser user,
        IOrderDirectory orders, SecretProtector protector, IOptions<PayOsOptions> options, CancellationToken ct)
    {
        var order = await TakeawaySandboxEndpoints.Scope(orderId, branchId, user, orders, ct);
        var tenant = TenantScope.Require(user);
        var rows = await db.Payments.AsNoTracking().Where(x => x.TenantId == tenant && x.OrderId == orderId).ToListAsync(ct);
        var attempt = rows.Count == 1 ? rows[0] : null;
        var valid = attempt is not null && attempt.BranchId == branchId && attempt.Method == PaymentProvider.PayOsCode
            && attempt.Amount == order.FinalAmount && attempt.RefundedAmount == 0 && order.Status is not ("Cancelled" or "NoShow" or "Unfulfillable");
        var state = rows.Count == 0 ? "None" : !valid ? "Unknown"
            : attempt!.IsPaid && attempt.PaidAtUtc != null && !attempt.RequiresReconciliation ? "Confirmed"
            : attempt.ProviderState == ProviderState.Failed ? "Failed" : "Pending";
        var available = await PayOsStaffPolicy.AvailableAsync(db, protector, options.Value, tenant, branchId, ct);
        return new { Version = 3, OrderId = orderId, BranchId = branchId, Amount = order.FinalAmount,
            ProviderCode = PaymentProvider.PayOsCode, ProviderIsTest = false, Available = available, State = state,
            CanCreate = available && rows.Count == 0 && order.PaymentStatus == "Unpaid" && order.Status is not ("Cancelled" or "NoShow" or "Unfulfillable" or "Completed"),
            Attempt = attempt is null ? null : PaymentResponse.From(attempt),
            QrPayload = valid && state == "Pending" && PayOs.IsCheckoutUrl(attempt!.CheckoutUrl) ? attempt.CheckoutUrl : null };
    }
    private static async Task<object> CreateAsync(Guid orderId, TakeawayQrRequest r, HttpContext http, PaymentsDbContext db,
        ICurrentUser user, IOrderDirectory orders, IBranchDirectory branches, SecretProtector protector, IOptions<PayOsOptions> options,
        PaymentProcessor processor, GatewayRegistry gateways, CancellationToken ct)
    {
        var order = await TakeawaySandboxEndpoints.Scope(orderId, r.BranchId, user, orders, ct);
        if (!await PayOsStaffPolicy.AvailableAsync(db, protector, options.Value, TenantScope.Require(user), r.BranchId, ct))
            throw new DependencyUnavailableException("payos-staff-channel");
        var key = http.Request.Headers["Idempotency-Key"].ToString();
        if (key.Length is < 16 or > 80 || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')
            || r.Amount <= 0 || r.Amount > 100_000_000 || r.Amount != decimal.Truncate(r.Amount) || r.Amount != order.FinalAmount)
            throw new ValidationFailedException("payment", "Stable key and exact whole VND amount required.");
        await OnlinePaymentCreation.CreateAsync(new(orderId, r.BranchId, r.Amount, PaymentProvider.PayOsCode, "takeaway-payos:v1:AICOS"),
            PaymentProvider.PayOsCode, db, processor, gateways, protector, user, branches, orders, ct, singleAttempt: true);
        return await ReadAsync(orderId, r.BranchId, db, user, orders, protector, options, ct);
    }
    private static async Task<object> QueryAsync(Guid orderId, Guid paymentId, Guid branchId, PaymentsDbContext db, ICurrentUser user,
        IOrderDirectory orders, SecretProtector protector, IOptions<PayOsOptions> options, PaymentProcessor processor, GatewayRegistry gateways, CancellationToken ct)
    {
        await TakeawaySandboxEndpoints.Scope(orderId, branchId, user, orders, ct);
        if (!PayOsStaffPolicy.MatchesChannel(options.Value, TenantScope.Require(user))) throw new DependencyUnavailableException("payos-channel");
        if (!await db.Payments.AnyAsync(x => x.Id == paymentId && x.TenantId == TenantScope.Require(user) && x.OrderId == orderId
            && x.BranchId == branchId && x.Method == PaymentProvider.PayOsCode, ct)) throw new NotFoundException("Payment", paymentId);
        await PaymentEndpoints.QueryAsync(paymentId, db, processor, gateways, user, ct);
        return await ReadAsync(orderId, branchId, db, user, orders, protector, options, ct);
    }
}
