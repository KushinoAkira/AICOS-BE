using AICOS.MenuCatalog.Api.Infrastructure;
using AICOS.MenuCatalog.Api.Persistence;

namespace AICOS.MenuCatalog.Api.Features.PublicMenu;

/// <summary>
/// UC-49 / UC-53: customers and guests read a branch menu without logging in (the QR menu and the takeaway menu).
/// Route: /api/v1/menu/public/branches/{branchId}/menu. Only published, available products appear (BR-16); the answer
/// comes from the Redis cache and is rebuilt after any change to the menu.
/// </summary>
public static class PublicMenuEndpoints
{
    public static void MapPublicMenuEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/public").WithTags("Public menu").AllowAnonymous();

        group.MapGet("/branches/{branchId:guid}/menu", GetMenuAsync);
    }

    private static Task<BranchMenuResponse> GetMenuAsync(Guid branchId, MenuCatalogDbContext db, MenuCache cache, CancellationToken ct) =>
        cache.GetOrBuildAsync(branchId, "public", () => MenuViews.BuildAsync(db, branchId, includeSoldOut: false, ct), ct);
}
