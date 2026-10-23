using AICOS.BuildingBlocks.Hosting;

namespace AICOS.ReportingAnalytics.Api;

/// <summary>Identity of the Reporting & Analytics Service. Also the assembly marker used by integration tests.</summary>
public sealed class ReportingAnalyticsService
{
    public static readonly AicosServiceInfo Info = new(
        Name: "reporting",
        DisplayName: "Reporting & Analytics Service",
        RoutePrefix: "reporting",
        Schema: "reporting");

    private ReportingAnalyticsService()
    {
    }
}
