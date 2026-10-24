using AICOS.BuildingBlocks.Security;

namespace AICOS.Ordering.Api.Features.Orders;

public static class OrderEndpoints
{
    public static RouteGroupBuilder MapOrderEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/", async (
            Guid sessionId,
            ICurrentUser user,
            OrderApplicationService service,
            CancellationToken cancellationToken) =>
                Results.Ok(await service.GetBySessionAsync(
                    sessionId,
                    user,
                    cancellationToken)))
            .RequireAuthorization(AicosPolicies.Staff)
            .Produces<SessionOrdersResponse>();

        api.MapPost("/", async (CreateOrderRequest request, ICurrentUser user, OrderApplicationService service, CancellationToken ct) =>
            Results.Created($"/api/v1/orders/{request.RequestId}", await service.CreateAsync(request, user, ct)))
            .RequireAuthorization(AicosPolicies.Staff).Produces<OrderResponse>(StatusCodes.Status201Created);
        api.MapGet("/{id:guid}", async (Guid id, ICurrentUser user, OrderApplicationService service, CancellationToken ct) =>
            Results.Ok(await service.GetAsync(id, user, ct)))
            .RequireAuthorization(AicosPolicies.Staff).Produces<OrderResponse>();
        return api;
    }
}
