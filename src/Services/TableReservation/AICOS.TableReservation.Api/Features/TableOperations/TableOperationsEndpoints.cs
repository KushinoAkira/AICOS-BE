using AICOS.BuildingBlocks.Security;

namespace AICOS.TableReservation.Api.Features.TableOperations;

public static class TableOperationsEndpoints
{
    private static readonly string[] OperationalRoles =
    [
        AicosRoles.SystemAdmin,
        AicosRoles.Manager,
        AicosRoles.Waiter,
        AicosRoles.Cashier,
    ];

    public static RouteGroupBuilder MapTableOperationsEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/sessions/{sessionId:guid}", async (
            Guid sessionId,
            ICurrentUser currentUser,
            TableOperationsService operations,
            CancellationToken cancellationToken) =>
                Results.Ok(await operations.GetSessionAsync(
                    sessionId,
                    currentUser,
                    cancellationToken)))
            .RequireAuthorization(AicosPolicies.Staff)
            .Produces<TableSessionResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/map", async (
            ICurrentUser currentUser,
            TableOperationsService operations,
            CancellationToken cancellationToken) =>
                Results.Ok(await operations.GetMapAsync(currentUser, cancellationToken)))
            .RequireAuthorization(AicosPolicies.Staff)
            .Produces<TableMapResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        api.MapPost("/sessions", async (
            OpenTableSessionRequest request,
            ICurrentUser currentUser,
            TableOperationsService operations,
            CancellationToken cancellationToken) =>
            {
                var session = await operations.OpenSessionAsync(
                    request,
                    currentUser,
                    cancellationToken);
                return Results.Created($"/api/v1/tables/sessions/{session.Id}", session);
            })
            .RequireAuthorization(policy => policy.RequireRole(OperationalRoles))
            .Produces<TableSessionResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapPost("/sessions/{sessionId:guid}/close", async (
            Guid sessionId,
            ICurrentUser currentUser,
            TableOperationsService operations,
            CancellationToken cancellationToken) =>
                Results.Ok(await operations.CloseSessionAsync(
                    sessionId,
                    currentUser,
                    cancellationToken)))
            .RequireAuthorization(policy => policy.RequireRole(OperationalRoles))
            .Produces<TableSessionResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapPost("/{tableId:guid}/cleaned", async (
            Guid tableId,
            ICurrentUser currentUser,
            TableOperationsService operations,
            CancellationToken cancellationToken) =>
                Results.Ok(await operations.MarkCleanedAsync(
                    tableId,
                    currentUser,
                    cancellationToken)))
            .RequireAuthorization(policy => policy.RequireRole(OperationalRoles))
            .Produces<CafeTableResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return api;
    }
}
