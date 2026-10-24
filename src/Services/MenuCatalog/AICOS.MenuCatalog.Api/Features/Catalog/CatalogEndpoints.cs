using AICOS.BuildingBlocks.Security;

namespace AICOS.MenuCatalog.Api.Features.Catalog;

public static class CatalogEndpoints
{
    public static RouteGroupBuilder MapCatalogEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/catalog", async (
            Guid? categoryId,
            string? search,
            ICurrentUser currentUser,
            CatalogQueryService catalog,
            CancellationToken cancellationToken) =>
                Results.Ok(await catalog.GetAsync(
                    currentUser,
                    categoryId,
                    search,
                    cancellationToken)))
            .RequireAuthorization(AicosPolicies.Staff)
            .Produces<CatalogResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return api;
    }
}
