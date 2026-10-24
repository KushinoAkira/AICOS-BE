using AICOS.BuildingBlocks.Security;

namespace AICOS.Payments.Api.Features.Billing;

public static class BillingEndpoints
{
    private static readonly string[] BillingRoles =
        [AicosRoles.SystemAdmin, AicosRoles.Manager, AicosRoles.Cashier];

    public static RouteGroupBuilder MapBillingEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost(
                "/bills",
                async (
                    CreateBillRequest request,
                    ICurrentUser currentUser,
                    BillingService service,
                    CancellationToken cancellationToken) =>
                {
                    var bill = await service.CreateAsync(
                        request,
                        currentUser,
                        cancellationToken);
                    return Results.Created($"/api/v1/payments/bills/{bill.Id}", bill);
                })
            .RequireAuthorization(policy => policy.RequireRole(BillingRoles))
            .Produces<BillResponse>(StatusCodes.Status201Created);

        api.MapGet(
                "/bills/{billId:guid}",
                async (
                    Guid billId,
                    ICurrentUser currentUser,
                    BillingService service,
                    CancellationToken cancellationToken) =>
                    Results.Ok(await service.GetAsync(
                        billId,
                        currentUser,
                        cancellationToken)))
            .RequireAuthorization(AicosPolicies.Staff)
            .Produces<BillResponse>();

        return api;
    }
}
