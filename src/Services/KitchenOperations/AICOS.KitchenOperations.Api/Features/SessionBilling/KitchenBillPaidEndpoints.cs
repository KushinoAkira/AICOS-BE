using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Billing;
using AICOS.KitchenOperations.Api.Features;
using AICOS.KitchenOperations.Api.Features.KitchenAdmission;
using AICOS.KitchenOperations.Api.Domain.Entities;
using AICOS.KitchenOperations.Api.Persistence;
using AICOS.KitchenOperations.Api.Services;
using Microsoft.EntityFrameworkCore;
namespace AICOS.KitchenOperations.Api.Features.SessionBilling;

public static class KitchenBillPaidEndpoints
{
    public static void MapKitchenBillPaid(this WebApplication app)
    {
        app.MapPost("/internal/session-billing/paid", ReceiveAsync).AllowAnonymous()
            .AddEndpointFilter(async (context, next) => BillingMachineAuth.Valid(context.HttpContext,
                context.HttpContext.RequestServices.GetRequiredService<IConfiguration>()) ? await next(context) : Results.Unauthorized());
    }
    private static async Task<IResult> ReceiveAsync(KitchenBillPaidNotice notice, KitchenOperationsDbContext db, IKitchenNotifier notifier,
        TimeProvider clock, ILoggerFactory logs, CancellationToken ct)
    {
        var p = notice.Payment;
        if (p.BillId != BillIdentity.For(p.TenantId, p.SessionId) || p.PaymentId == Guid.Empty || notice.OrderIds.Count is < 1 or > 1000
            || notice.OrderIds.Distinct().Count() != notice.OrderIds.Count) throw new ValidationFailedException("notice", "Invalid bill payment manifest.");
        foreach (var order in notice.OrderIds.Order())
        {
            var ticket = await KitchenTicketWrites.RunAsync(db, p.TenantId, order, async () =>
            {
                var found = await TicketLookup.ByOrderAsync(db, p.TenantId, order, ct);
                if (found is null)
                {
                    if (!await db.EarlyPayments.AnyAsync(e => e.TenantId == p.TenantId && e.OrderId == order, ct))
                        db.EarlyPayments.Add(new EarlyPayment { TenantId = p.TenantId, OrderId = order, PaidAtUtc = p.PaidAtUtc });
                }
                else
                {
                    if (found.BranchId != p.BranchId) throw new ConflictException("Kitchen branch differs.", "billing.scope");
                    found.MarkAsPaid();
                    if (found.FulfillmentMode == AICOS.Contracts.Serving.FulfillmentModes.WholeOrder && found.RecoveryHoldReason is null)
                        found.TryGenerateCupStickers(clock.GetUtcNow().UtcDateTime, out _);
                }
                await db.SaveChangesAsync(ct); return found;
            }, ct);
            if (ticket is not null && ticket.IsEligibleForBar && ticket.RecoveryHoldReason is null)
                await KitchenEndpoints.SafeAsync(logs.CreateLogger("BillAdmission"), () => notifier.NotifyNewTicketAsync(ticket.TenantId, ticket.BranchId, TicketMapper.ToDto(ticket), ct));
        }
        return Results.Ok(new { p.BillId, p.PaymentId, Applied = true });
    }
}
