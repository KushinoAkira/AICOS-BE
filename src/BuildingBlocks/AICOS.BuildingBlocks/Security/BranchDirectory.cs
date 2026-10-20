using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Http;

namespace AICOS.BuildingBlocks.Security;

/// <summary>What another service needs to know about a branch.</summary>
public sealed record BranchInfo(Guid Id, string Name, bool IsActive);

/// <summary>
/// Asks the Identity service whether a branch exists inside the caller's own tenant (BR-04, BR-08).
/// Identity answers 404 for a branch of another brand, so a BrandAdmin can never point a request at a foreign branch.
/// </summary>
public interface IBranchDirectory
{
    /// <summary>Throws a 400 when the branch does not exist in the caller's tenant.</summary>
    Task<BranchInfo> GetAsync(Guid branchId, CancellationToken cancellationToken = default);
}

internal sealed class HttpBranchDirectory(HttpClient http) : IBranchDirectory
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<BranchInfo> GetAsync(Guid branchId, CancellationToken cancellationToken = default)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync($"/api/v1/identity/branches/{branchId}", cancellationToken);
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
                    throw new ValidationFailedException("branchId", $"Branch '{branchId}' does not exist in your brand.");
                case HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized:
                    throw new ForbiddenException("You cannot access that branch.", "branch.forbidden");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new DependencyUnavailableException("identity");
            }

            var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json, cancellationToken);
            return new BranchInfo(
                branchId,
                body.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty,
                body.TryGetProperty("status", out var status) && string.Equals(status.GetString(), "Active", StringComparison.OrdinalIgnoreCase));
        }
    }
}

public static class BranchDirectoryExtensions
{
    /// <summary>Registers <see cref="IBranchDirectory"/> over <c>Services:Identity:BaseUrl</c> (needs the caller's bearer token, which is forwarded).</summary>
    public static IServiceCollection AddAicosBranchDirectory(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddAicosServiceClient<IBranchDirectory, HttpBranchDirectory>(configuration, "Identity");
        return services;
    }
}

/// <summary>Branch scoping with the tenant check that <see cref="BranchAccess"/> cannot do on its own.</summary>
public static class BranchScope
{
    /// <summary>
    /// Like <see cref="BranchAccess.Resolve"/>, and when a BrandAdmin names the branch, confirms it belongs to their tenant (and,
    /// for writes, is not suspended - BR-08). Staff roles get the branch of their token without any call.
    /// </summary>
    public static async Task<Guid> ResolveAsync(
        ICurrentUser user, IBranchDirectory directory, Guid? requestedBranchId, bool requireActive, CancellationToken cancellationToken)
    {
        var branchId = BranchAccess.Resolve(user, requestedBranchId);
        if (user.IsInRole(AicosRoles.BrandAdmin))
        {
            await EnsureUsableAsync(directory, branchId, requireActive, cancellationToken);
        }

        return branchId;
    }

    /// <summary>For a BrandAdmin-supplied branch id that is not the main subject of the request (e.g. an item of a list).</summary>
    public static async Task EnsureUsableAsync(IBranchDirectory directory, Guid branchId, bool requireActive, CancellationToken cancellationToken)
    {
        var branch = await directory.GetAsync(branchId, cancellationToken);
        if (requireActive && !branch.IsActive)
        {
            throw new BusinessRuleException("branch.suspended", $"Branch '{branch.Name}' is suspended and cannot be changed.");
        }
    }
}
