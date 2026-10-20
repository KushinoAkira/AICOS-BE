using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Http;

namespace AICOS.BuildingBlocks.Security;

/// <summary>What another service needs to know about a login account.</summary>
public sealed record UserInfo(Guid Id, string FullName, string Role, Guid? BranchId, bool IsActive);

/// <summary>
/// Asks the Identity service about an account of the caller own tenant. Identity applies the caller visibility rules: a
/// BrandAdmin sees every account of the brand, a BranchManager only the Barista and Cashier accounts of their own branch,
/// so an account outside that is simply "not found".
/// </summary>
public interface IUserDirectory
{
    /// <summary>Throws a 400 when the account does not exist (or is not visible to the caller).</summary>
    Task<UserInfo> GetAsync(Guid userId, CancellationToken cancellationToken = default);
}

internal sealed class HttpUserDirectory(HttpClient http) : IUserDirectory
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<UserInfo> GetAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync($"/api/v1/identity/users/{userId}", cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new DependencyUnavailableException("identity");
        }

        using (response)
        {
            switch (response.StatusCode)
            {
                case HttpStatusCode.NotFound:
                    throw new ValidationFailedException("userId", $"User account '{userId}' does not exist (or is not one of your staff).");
                case HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized:
                    throw new ForbiddenException("You cannot access that account.", "user.forbidden");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new DependencyUnavailableException("identity");
            }

            var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json, cancellationToken);
            return new UserInfo(
                userId,
                body.TryGetProperty("fullName", out var name) ? name.GetString() ?? string.Empty : string.Empty,
                body.TryGetProperty("role", out var role) ? role.GetString() ?? string.Empty : string.Empty,
                body.TryGetProperty("branchId", out var branch) && branch.ValueKind == JsonValueKind.String && Guid.TryParse(branch.GetString(), out var branchId) ? branchId : null,
                body.TryGetProperty("status", out var status) && string.Equals(status.GetString(), "Active", StringComparison.OrdinalIgnoreCase));
        }
    }
}

public static class UserDirectoryExtensions
{
    /// <summary>Registers <see cref="IUserDirectory"/> over <c>Services:Identity:BaseUrl</c> (the caller bearer token is forwarded).</summary>
    public static IServiceCollection AddAicosUserDirectory(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddAicosServiceClient<IUserDirectory, HttpUserDirectory>(configuration, "Identity");
        return services;
    }
}
