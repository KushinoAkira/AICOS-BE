using AICOS.BuildingBlocks.Exceptions;

namespace AICOS.BuildingBlocks.Security;

/// <summary>
/// BR-58 branch scoping, shared by every service:
/// staff (Manager, Cashier, WarehouseStaff) only ever see/act on the branch in their token;
/// a BrandAdmin works chain-wide and must say which branch they mean.
/// </summary>
public static class BranchAccess
{
    /// <summary>
    /// The branch a request operates on. Staff: their own branch (a different <paramref name="requestedBranchId"/> is 403).
    /// BrandAdmin: <paramref name="requestedBranchId"/>, which is then required (400).
    /// </summary>
    public static Guid Resolve(ICurrentUser user, Guid? requestedBranchId)
    {
        if (user.IsInRole(AicosRoles.BrandAdmin))
        {
            return requestedBranchId
                ?? throw new ValidationFailedException("branchId", "branchId is required for a BrandAdmin (staff use the branch of their account).");
        }

        var own = user.BranchId ?? throw new ForbiddenException("Your account is not assigned to a branch.");
        if (requestedBranchId is not null && requestedBranchId != own)
        {
            throw new ForbiddenException("You can only access data of your own branch.", "branch.forbidden");
        }

        return own;
    }

    /// <summary>
    /// Like <see cref="Resolve"/> but for list endpoints: a BrandAdmin may omit the branch to see every branch (returns null).
    /// </summary>
    public static Guid? ResolveFilter(ICurrentUser user, Guid? requestedBranchId) =>
        user.IsInRole(AicosRoles.BrandAdmin) ? requestedBranchId : Resolve(user, requestedBranchId);

    /// <summary>403 when a non-admin touches a record of another branch.</summary>
    public static void EnsureCanAccess(ICurrentUser user, Guid branchId)
    {
        if (!user.IsInRole(AicosRoles.BrandAdmin) && user.BranchId != branchId)
        {
            throw new ForbiddenException("You can only access data of your own branch.", "branch.forbidden");
        }
    }
}
