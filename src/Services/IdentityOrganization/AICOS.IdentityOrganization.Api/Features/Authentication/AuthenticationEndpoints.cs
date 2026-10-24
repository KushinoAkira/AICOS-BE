using AICOS.BuildingBlocks.Security;

namespace AICOS.IdentityOrganization.Api.Features.Authentication;

public static class AuthenticationEndpoints
{
    public static RouteGroupBuilder MapAuthenticationEndpoints(this RouteGroupBuilder api)
    {
        var auth = api.MapGroup("/auth");
        auth.MapPost("/login", async (
            LoginRequest request,
            AuthenticationService authentication,
            CancellationToken cancellationToken) =>
                Results.Ok(await authentication.LoginAsync(request, cancellationToken)))
            .AllowAnonymous()
            .Produces<LoginResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        auth.MapPost("/refresh", async (
            RefreshRequest request,
            SessionService sessions,
            CancellationToken cancellationToken) =>
                Results.Ok(await sessions.RefreshAsync(request, cancellationToken)))
            .AllowAnonymous()
            .Produces<RefreshResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status409Conflict);

        auth.MapPost("/logout", async (
            LogoutRequest request,
            SessionService sessions,
            CancellationToken cancellationToken) =>
            {
                await sessions.LogoutAsync(request, cancellationToken);
                return Results.NoContent();
            })
            .AllowAnonymous()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        auth.MapGet("/me", async (
            ICurrentUser currentUser,
            SessionService sessions,
            CancellationToken cancellationToken) =>
                Results.Ok(await sessions.GetCurrentAsync(currentUser, cancellationToken)))
            .RequireAuthorization(AicosPolicies.Staff)
            .Produces<StaffProfileResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return api;
    }
}
