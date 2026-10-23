using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;

namespace AICOS.IdentityOrganization.Api.Features.Common;

/// <summary>BR-04: every brand-level request works inside the tenant carried by the caller's token.</summary>
internal static class Tenancy
{
    public static Guid Require(ICurrentUser user) =>
        user.TenantId ?? throw new ForbiddenException("Your account does not belong to a tenant.", "tenant.required");
}
