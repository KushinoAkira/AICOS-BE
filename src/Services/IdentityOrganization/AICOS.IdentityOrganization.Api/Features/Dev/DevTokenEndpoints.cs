using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;

namespace AICOS.IdentityOrganization.Api.Features.Dev;

/// <summary>
/// DEVELOPMENT ONLY: issues a JWT for any role so endpoints can be tested before the real login (UC-01) exists.
/// Mapped only when ASPNETCORE_ENVIRONMENT=Development. Remove once login is implemented.
/// </summary>
public static class DevTokenEndpoints
{
    public sealed record DevTokenRequest(string? Role, Guid? BranchId, string? Name);

    public static void MapDevTokenEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/dev/token", (DevTokenRequest request, JwtTokenService tokens) =>
        {
            var role = AicosRoles.All.FirstOrDefault(r => string.Equals(r, request.Role, StringComparison.OrdinalIgnoreCase))
                ?? throw new ValidationFailedException(nameof(request.Role), $"Role must be one of: {string.Join(", ", AicosRoles.All)}.");

            var (token, expiresAtUtc) = tokens.CreateAccessToken(
                Guid.NewGuid(), request.Name ?? $"Dev {role}", role, request.BranchId);

            return new { accessToken = token, expiresAtUtc, role };
        })
        .WithTags("Dev (Development only)")
        .AllowAnonymous();
    }
}
