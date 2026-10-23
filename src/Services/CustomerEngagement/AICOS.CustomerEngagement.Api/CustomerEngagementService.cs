using AICOS.BuildingBlocks.Hosting;

namespace AICOS.CustomerEngagement.Api;

/// <summary>Identity of the Customer Engagement Service. Also the assembly marker used by integration tests.</summary>
public sealed class CustomerEngagementService
{
    public static readonly AicosServiceInfo Info = new(
        Name: "customer",
        DisplayName: "Customer Engagement Service",
        RoutePrefix: "customers",
        Schema: "customer");

    private CustomerEngagementService()
    {
    }
}
