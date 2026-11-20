using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.Payments.Api.Domain;
using AICOS.Payments.Api.Infrastructure;
using AICOS.Payments.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AICOS.Payments.Api.Features.Payments;

/// <summary>Opt-in ZaloPay sandbox adapter; all funds/status remain in the existing order payment ledger.</summary>
public static class TakeawaySandboxEndpoints
{
    public static void MapTakeawaySandbox(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/staff/takeaway/{orderId:guid}/online")
            .RequireAuthorization(p => p.RequireRole(PaymentAccess.CounterRoles));
        group.MapGet("", ReadAsync);
        group.MapPost("", CreateAsync).AddEndpointFilter<Features.CashierShifts.CashActorFilter>();
        group.MapPost("/{paymentId:guid}/query", QueryAsync).AddEndpointFilter<Features.CashierShifts.CashActorFilter>();
    }

    private static async Task<bool> Enabled(PaymentsDbContext db, Guid tenant, Guid branch, IConfiguration config,
        IHostEnvironment env, ZaloPayOptions options, CancellationToken ct)
    {
        if (!env.IsDevelopment() || !config.GetValue<bool>("TakeawayQr:ZaloPaySandboxEnabled") || !options.Live || options.TestPage
            || !Uri.TryCreate(options.ApiUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.Host != "sb-openapi.zalopay.vn" || uri.Port != 443 || uri.UserInfo != "" || uri.Query != "" || uri.Fragment != "" || uri.AbsolutePath != "/") return false;
        return await db.PaymentProviders.AnyAsync(x => x.Code == ZaloPay.Code && x.Mode == ProviderMode.Sandbox && x.IsEnabled, ct)
            && await MerchantResolver.ResolveAsync(db, tenant, branch, ZaloPay.Code, ct) is not null;
    }

    internal static async Task<OrderInfo> Scope(Guid orderId, Guid branchId, ICurrentUser user, IOrderDirectory orders, CancellationToken ct)
    {
        BranchAccess.EnsureCanAccess(user, branchId);
        var order = await orders.GetAsync(orderId, ct);
        if (order.Id != orderId || order.BranchId != branchId || order.IsTableSession)
            throw new ConflictException("Use an order without a table session in this branch.", "payment.takeaway_required");
        return order;
    }

    private static async Task<object> ReadAsync(Guid orderId, Guid branchId, PaymentsDbContext db, ICurrentUser user,
        IOrderDirectory orders, IConfiguration config, IHostEnvironment env, IOptions<ZaloPayOptions> options, TimeProvider clock, CancellationToken ct)
    {
        var order = await Scope(orderId, branchId, user, orders, ct); var tenant = TenantScope.Require(user);
        var rows = await db.Payments.AsNoTracking().Where(x => x.TenantId == tenant && x.OrderId == orderId).ToListAsync(ct);
        var available = await Enabled(db, tenant, branchId, config, env, options.Value, ct);
        var attempt = rows.Count == 1 ? rows[0] : null;
        var valid = attempt is not null && attempt.BranchId == branchId && attempt.Amount == order.FinalAmount
            && attempt.Method == ZaloPay.Code && attempt.RefundedAmount == 0 && order.Status is not ("Cancelled" or "NoShow" or "Unfulfillable");
        var state = rows.Count == 0 ? "None" : !valid ? "Unknown"
            : attempt!.IsPaid && attempt.PaidAtUtc != null && !attempt.RequiresReconciliation ? "Confirmed"
            : attempt.ProviderState == ProviderState.Failed ? "Failed"
            : attempt.Status == PaymentStatus.Pending && attempt.ExpiresAtUtc > clock.GetUtcNow().UtcDateTime ? "Pending" : "Unknown";
        var open = order.Status is not ("Cancelled" or "NoShow" or "Unfulfillable" or "Completed") && order.PaymentStatus == "Unpaid";
        var checkout = valid && state == "Pending" && available && Uri.TryCreate(attempt!.CheckoutUrl, UriKind.Absolute, out var url)
            && url.Scheme == "https" && url.Host == "qcgateway.zalopay.vn" && url.Port == 443 && url.UserInfo == "" ? attempt.CheckoutUrl : null;
        return new { Version = 2, OrderId = orderId, BranchId = branchId, Amount = order.FinalAmount,
            ProviderCode = ZaloPay.Code, ProviderIsTest = true, Available = available, State = state,
            CanCreate = available && open && rows.Count == 0, Attempt = attempt is null ? null : PaymentResponse.From(attempt),
            QrPayload = checkout, ReasonCode = !available ? "payment.sandbox_unavailable" : state == "Unknown" ? "payment.reconciliation_required" : null };
    }

    private static async Task<object> CreateAsync(Guid orderId, TakeawayQrRequest r, HttpContext http, PaymentsDbContext db,
        ICurrentUser user, IOrderDirectory orders, IBranchDirectory branches, IConfiguration config, IHostEnvironment env,
        IOptions<ZaloPayOptions> options, PaymentProcessor processor, GatewayRegistry gateways, SecretProtector protector, TimeProvider clock, CancellationToken ct)
    {
        var order = await Scope(orderId, r.BranchId, user, orders, ct);
        if (!await Enabled(db, TenantScope.Require(user), r.BranchId, config, env, options.Value, ct))
            throw new ConflictException("Verified ZaloPay sandbox is unavailable.", "payment.sandbox_unavailable");
        var key = http.Request.Headers["Idempotency-Key"].ToString();
        if (key.Length is < 16 or > 80 || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')
            || r.Amount <= 0 || r.Amount > 100_000_000 || r.Amount != decimal.Truncate(r.Amount) || r.Amount != order.FinalAmount)
            throw new ValidationFailedException("payment", "A stable key and exact whole VND amount are required.");
        await OnlinePaymentCreation.CreateAsync(new(orderId, r.BranchId, r.Amount, ZaloPay.Code, "AICOS takeaway sandbox"),
            ZaloPay.Code, db, processor, gateways, protector, user, branches, orders, ct, singleAttempt: true);
        return await ReadAsync(orderId, r.BranchId, db, user, orders, config, env, options, clock, ct);
    }

    private static async Task<object> QueryAsync(Guid orderId, Guid paymentId, Guid branchId, PaymentsDbContext db,
        ICurrentUser user, IOrderDirectory orders, IConfiguration config, IHostEnvironment env, IOptions<ZaloPayOptions> options,
        PaymentProcessor processor, GatewayRegistry gateways, TimeProvider clock, CancellationToken ct)
    {
        await Scope(orderId, branchId, user, orders, ct);
        if (!await Enabled(db, TenantScope.Require(user), branchId, config, env, options.Value, ct))
            throw new ConflictException("Verified ZaloPay sandbox is unavailable.", "payment.sandbox_unavailable");
        var tenant = TenantScope.Require(user);
        if (!await db.Payments.AnyAsync(x => x.Id == paymentId && x.OrderId == orderId && x.TenantId == tenant && x.BranchId == branchId && x.Method == ZaloPay.Code, ct))
            throw new NotFoundException("Payment", paymentId);
        await PaymentEndpoints.QueryAsync(paymentId, db, processor, gateways, user, ct);
        return await ReadAsync(orderId, branchId, db, user, orders, config, env, options, clock, ct);
    }
}
