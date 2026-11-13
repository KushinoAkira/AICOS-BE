using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Billing;
using AICOS.Payments.Api.Features.Payments;
using AICOS.Payments.Api.Persistence;
using Microsoft.EntityFrameworkCore;
namespace AICOS.Payments.Api.Features.SessionBilling;

public static class SessionBillEndpoints
{
    public static void AddSessionBilling(this WebApplicationBuilder builder)
    {
        builder.Services.AddScoped<BillLedgerService>(); builder.Services.AddScoped<BillWebhookReceiver>();
        builder.Services.AddScoped<BillOutboxDispatcher>(); builder.Services.AddHostedService<BillOutboxWorker>();
        AICOS.BuildingBlocks.Http.ServiceClientExtensions.AddAicosServiceClient<IBillOrderingClient, HttpBillOrderingClient>(builder.Services, builder.Configuration, "Order");
        AICOS.BuildingBlocks.Http.ServiceClientExtensions.AddAicosServiceClient<IBillKitchenClient, HttpBillKitchenClient>(builder.Services, builder.Configuration, "Kitchen");
    }
    public static void MapSessionBilling(this WebApplication app, RouteGroupBuilder api)
    {
        var bills = api.MapGroup("/bills").WithTags("Session bills").AddEndpointFilter(async (context, next) =>
            BillingMachineAuth.Enabled(context.HttpContext.RequestServices.GetRequiredService<IConfiguration>()) ? await next(context) : Results.StatusCode(503));
        bills.MapGet("/", (Guid sessionId, ICurrentUser user, BillLedgerService ledger, CancellationToken ct) => ledger.GetAsync(sessionId, user, ct)).RequireAuthorization(AicosPolicies.Staff);
        bills.MapGet("/{id:guid}", (Guid id, ICurrentUser user, BillLedgerService ledger, CancellationToken ct) => ledger.GetByIdAsync(id, user, false, ct)).RequireAuthorization(AicosPolicies.Staff);
        bills.MapGet("/{id:guid}/invoice", (Guid id, ICurrentUser user, BillLedgerService ledger, CancellationToken ct) => ledger.GetByIdAsync(id, user, true, ct)).RequireAuthorization(b => b.RequireRole(PaymentAccess.CounterRoles));
        bills.MapPost("/{id:guid}/payments/cash", (Guid id, CollectBillRequest request, HttpContext http, ICurrentUser user, BillLedgerService ledger, CancellationToken ct) =>
            ledger.CollectAsync(id, request, http.Request.Headers["Idempotency-Key"].ToString().Trim(), "Cash", user, ct)).RequireAuthorization(b => b.RequireRole(PaymentAccess.CounterRoles));
        bills.MapPost("/{id:guid}/payments/qr", (Guid id, CollectBillRequest request, HttpContext http, ICurrentUser user, BillLedgerService ledger, CancellationToken ct) =>
            ledger.CollectAsync(id, request, http.Request.Headers["Idempotency-Key"].ToString().Trim(), "QR", user, ct)).RequireAuthorization(b => b.RequireRole(PaymentAccess.CounterRoles));
        bills.MapPost("/webhooks/{provider}", (string provider, WebhookRequest request, BillWebhookReceiver receiver, CancellationToken ct) => receiver.ReceiveAsync(provider, request, ct)).AllowAnonymous();
        var machine = app.MapGroup("/internal/session-billing").AllowAnonymous().AddEndpointFilter(async (context, next) =>
            BillingMachineAuth.Valid(context.HttpContext, context.HttpContext.RequestServices.GetRequiredService<IConfiguration>()) ? await next(context) : Results.Unauthorized());
        machine.MapGet("/bills/{id:guid}/proof", ProofAsync);
        machine.MapPost("/bills/{id:guid}/seal", SealAsync);
    }
    private static async Task<BillCloseProof> ProofAsync(Guid id, PaymentsDbContext db, CancellationToken ct)
    {
        var bill = await db.SessionBills.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct) ?? throw new NotFoundException("Bill", id);
        return await Proof(db, bill, ct);
    }
    private static async Task<BillCloseProof> Proof(PaymentsDbContext db, SessionBill bill, CancellationToken ct)
    {
        if (bill.Status is not ("PAID" or "CLOSED") || bill.PaidPaymentId is null || !bill.OrderingAcknowledged || bill.NeedsReconciliation || await BillLedgerService.LegacyRiskAsync(db, bill, ct)
            || await db.BillAttempts.AnyAsync(a => a.BillId == bill.Id && a.Status == "PENDING", ct))
            throw new ConflictException("Bill settlement is not verified.", "billing.not_settled");
        var receipt = await db.BillAttempts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == bill.PaidPaymentId && a.BillId == bill.Id && a.TenantId == bill.TenantId, ct);
        if (receipt is null || receipt.Status != "SUCCEEDED" || receipt.Amount != bill.Total || receipt.PaidAtUtc is null)
            throw new ConflictException("Received money proof is invalid.", "billing.not_settled");
        return new(bill.TenantId, bill.BranchId, bill.SessionId, bill.Id, bill.PaidPaymentId.Value, bill.Fingerprint);
    }
    private static Task<BillCloseProof> SealAsync(Guid id, BillSealRequest request, PaymentsDbContext db, BillLedgerService ledger, TimeProvider time, CancellationToken ct) =>
        BillLock.RunAsync(db, id, async () =>
        {
            db.ChangeTracker.Clear();
            var bill = await db.SessionBills.FirstOrDefaultAsync(b => b.Id == id, ct) ?? throw new NotFoundException("Bill", id);
            var proof = await Proof(db, bill, ct);
            if (proof != request.Proof) throw new ConflictException("Close proof changed.", "billing.stale");
            if (bill.Status != "CLOSED")
            {
                bill.Status = "CLOSED"; bill.ClosedAtUtc = time.GetUtcNow().UtcDateTime; bill.Version++;
                ledger.AddJournal(bill, "CLOSED", proof, bill.ClosedAtUtc.Value);
                await db.SaveChangesAsync(ct);
            }
            return proof;
        }, ct);
}
