using AICOS.BuildingBlocks.Hosting;

namespace AICOS.IdentityOrganization.Api;

/// <summary>Identity of the Identity & Organization Service. Also the assembly marker used by integration tests.</summary>
public sealed class IdentityOrganizationService
{
    public static readonly AicosServiceInfo Info = new(
        Name: "identity",
        DisplayName: "Identity & Organization Service",
        RoutePrefix: "identity",
        Schema: "public");

    private IdentityOrganizationService()
    {
    }
}
