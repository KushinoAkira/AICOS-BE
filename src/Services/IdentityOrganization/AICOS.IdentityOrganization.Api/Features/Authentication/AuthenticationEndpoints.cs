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

        return api;
    }
}
