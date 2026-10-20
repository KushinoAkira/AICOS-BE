using AICOS.BuildingBlocks.Exceptions;

namespace AICOS.BuildingBlocks.Security;

/// <summary>BR-04: every brand-level request works inside the tenant carried by the caller token. Shared by all services.</summary>
public static class TenantScope
{
    /// <summary>The caller tenant. 403 for tokens without one (the SuperAdmin, who never reads brand data - BR-06).</summary>
    public static Guid Require(ICurrentUser user) =>
        user.TenantId ?? throw new ForbiddenException("Your account does not belong to a tenant.", "tenant.required");
}
