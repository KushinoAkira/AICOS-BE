using AICOS.BuildingBlocks.Security;

namespace AICOS.KitchenOperations.Api.Features.Preparation;

public static class PreparationEndpoints
{
    public static RouteGroupBuilder MapPreparationEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet(
                "/queue",
                async (
                    ICurrentUser currentUser,
                    PreparationService service,
                    CancellationToken cancellationToken) =>
                    Results.Ok(await service.GetQueueAsync(currentUser, cancellationToken)))
            .RequireAuthorization(AicosPolicies.Staff)
            .Produces<PreparationQueueResponse>();

        api.MapPatch(
                "/items/{itemId:guid}/status",
                async (
                    Guid itemId,
                    UpdatePreparationStatusRequest request,
                    ICurrentUser currentUser,
                    PreparationService service,
                    CancellationToken cancellationToken) =>
                    Results.Ok(await service.UpdateAsync(
                        itemId,
                        request,
                        currentUser,
                        cancellationToken)))
            .RequireAuthorization(policy => policy.RequireRole(
                AicosRoles.SystemAdmin,
                AicosRoles.Manager))
            .Produces<PreparationItemResponse>();

        return api;
    }
}
