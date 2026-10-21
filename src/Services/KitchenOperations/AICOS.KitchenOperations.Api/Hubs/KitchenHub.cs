using AICOS.BuildingBlocks.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace AICOS.KitchenOperations.Api.Hubs;

/// <summary>
/// Real-time channel for the bar screens (KDS) and the cashier screens (POS) of a branch (NFR-22). The caller must be signed in (the browser
/// sends the token as <c>access_token</c> in the query). A group name carries the brand, so a user can only ever join groups of their own
/// brand; staff of a branch can only join their own branch.
/// </summary>
[Authorize]
public class KitchenHub(ILogger<KitchenHub> logger) : Hub
{
    public static string KitchenGroup(Guid tenantId, Guid branchId) => $"kitchen-{tenantId}-{branchId}";

    public static string PosGroup(Guid tenantId, Guid branchId) => $"pos-{tenantId}-{branchId}";

    /// <summary>Bar screens: Barista, Branch Manager, Brand Admin.</summary>
    public Task JoinKitchenGroup(string branchId) =>
        JoinAsync(branchId, KitchenGroup, AicosRoles.Barista, AicosRoles.BranchManager, AicosRoles.BrandAdmin);

    /// <summary>Cashier screens: Cashier, Branch Manager, Brand Admin.</summary>
    public Task JoinPosGroup(string branchId) =>
        JoinAsync(branchId, PosGroup, AicosRoles.Cashier, AicosRoles.BranchManager, AicosRoles.BrandAdmin);

    private async Task JoinAsync(string branchIdText, Func<Guid, Guid, string> groupName, params string[] roles)
    {
        var user = Context.User ?? throw new HubException("Not signed in.");
        if (!roles.Any(user.IsInRole))
        {
            throw new HubException("Your role cannot join this channel.");
        }

        if (!Guid.TryParse(branchIdText, out var branchId))
        {
            throw new HubException("Invalid branch.");
        }

        if (!Guid.TryParse(user.FindFirst(AicosClaimTypes.TenantId)?.Value, out var tenantId))
        {
            throw new HubException("Your account has no brand.");
        }

        if (!user.IsInRole(AicosRoles.BrandAdmin)
            && (!Guid.TryParse(user.FindFirst(AicosClaimTypes.BranchId)?.Value, out var own) || own != branchId))
        {
            throw new HubException("You can only join your own branch.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, groupName(tenantId, branchId));
        logger.LogInformation("Client {ConnectionId} joined a branch channel.", Context.ConnectionId);
    }
}
