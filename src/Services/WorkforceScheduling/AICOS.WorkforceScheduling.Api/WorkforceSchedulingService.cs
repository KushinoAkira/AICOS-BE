using AICOS.BuildingBlocks.Hosting;

namespace AICOS.WorkforceScheduling.Api;

/// <summary>Identity of the Workforce & Scheduling Service. Also the assembly marker used by integration tests.</summary>
public sealed class WorkforceSchedulingService
{
    public static readonly AicosServiceInfo Info = new(
        Name: "workforce",
        DisplayName: "Workforce & Scheduling Service",
        RoutePrefix: "workforce",
        Schema: "public");

    private WorkforceSchedulingService()
    {
    }
}
