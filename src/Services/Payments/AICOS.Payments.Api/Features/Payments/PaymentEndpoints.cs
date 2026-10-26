using AICOS.BuildingBlocks.Security;

namespace AICOS.Payments.Api.Features.Payments;

public static class PaymentEndpoints
{
    private static readonly string[] CashierRoles =
        [AicosRoles.SystemAdmin, AicosRoles.Manager, AicosRoles.Cashier];

    public static RouteGroupBuilder MapPaymentEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/bills/{billId:guid}/payments/cash", async (
            Guid billId,
            CreatePaymentRequest request,
            ICurrentUser currentUser,
            PaymentService service,
            CancellationToken cancellationToken) =>
        {
            var payment = await service.CreateCashAsync(
                billId, request, currentUser, cancellationToken);
            return Results.Created($"/api/v1/payments/payments/{payment.Id}", payment);
        }).RequireAuthorization(policy => policy.RequireRole(CashierRoles));

        api.MapPost("/bills/{billId:guid}/payments/qr", async (
            Guid billId,
            CreatePaymentRequest request,
            ICurrentUser currentUser,
            PaymentService service,
            CancellationToken cancellationToken) =>
        {
            var payment = await service.CreateQrAsync(
                billId, request, currentUser, cancellationToken);
            return Results.Created($"/api/v1/payments/payments/{payment.Id}", payment);
        }).RequireAuthorization(policy => policy.RequireRole(CashierRoles));

        api.MapPost("/payments/{paymentId:guid}/confirm", async (
            Guid paymentId,
            ConfirmBankTransferRequest request,
            ICurrentUser currentUser,
            PaymentService service,
            CancellationToken cancellationToken) =>
                Results.Ok(await service.ConfirmBankTransferAsync(
                    paymentId, request, currentUser, cancellationToken)))
            .RequireAuthorization(policy => policy.RequireRole(CashierRoles));

        api.MapPost("/bills/{billId:guid}/close", async (
            Guid billId,
            CloseBillRequest request,
            ICurrentUser currentUser,
            PaymentService service,
            CancellationToken cancellationToken) =>
                Results.Ok(await service.CloseBillAsync(
                    billId, request, currentUser, cancellationToken)))
            .RequireAuthorization(policy => policy.RequireRole(CashierRoles));

        api.MapGet("/sessions/{sessionId:guid}/settlement-status", async (
            Guid sessionId,
            ICurrentUser currentUser,
            PaymentService service,
            CancellationToken cancellationToken) =>
                Results.Ok(await service.GetSettlementStatusAsync(
                    sessionId, currentUser, cancellationToken)))
            .RequireAuthorization(AicosPolicies.Staff);

        return api;
    }
}
