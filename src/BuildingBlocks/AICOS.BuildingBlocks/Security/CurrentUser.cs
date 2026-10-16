namespace AICOS.BuildingBlocks.Security;

/// <summary>The authenticated caller of the current request (resolved from the JWT).</summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    Guid? UserId { get; }
    string? Name { get; }
    string? Role { get; }

    /// <summary>Branch the user is assigned to. Null for chain-wide roles (SystemAdmin) and customers.</summary>
    Guid? BranchId { get; }

    bool IsInRole(string role);
}

internal sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private System.Security.Claims.ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public Guid? UserId => ParseGuid(AicosClaimTypes.UserId);

    public string? Name => Principal?.FindFirst(AicosClaimTypes.Name)?.Value;

    public string? Role => Principal?.FindFirst(AicosClaimTypes.Role)?.Value;

    public Guid? BranchId => ParseGuid(AicosClaimTypes.BranchId);

    public bool IsInRole(string role) => Principal?.IsInRole(role) == true;

    private Guid? ParseGuid(string claimType) =>
        Guid.TryParse(Principal?.FindFirst(claimType)?.Value, out var id) ? id : null;
}
