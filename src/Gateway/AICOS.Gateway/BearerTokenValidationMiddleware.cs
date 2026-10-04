using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;

namespace AICOS.Gateway;

/// <summary>
/// Edge token validation: if a request carries an <c>Authorization</c> header, the JWT must be valid (signature, issuer,
/// audience, expiry) or the request is rejected with 401 right here, without bothering the downstream service.
/// Requests without a token pass through (guest QR ordering, login, public menu); each service enforces its own
/// authorization policies.
/// </summary>
public sealed class BearerTokenValidationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Headers.ContainsKey("Authorization"))
        {
            var result = await context.AuthenticateAsync();
            if (!result.Succeeded)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = "Bearer";
                await context.Response.WriteAsJsonAsync(
                    new ProblemDetails
                    {
                        Status = StatusCodes.Status401Unauthorized,
                        Title = "Invalid or expired access token",
                    },
                    options: null,
                    contentType: "application/problem+json");
                return;
            }
        }

        await next(context);
    }
}
