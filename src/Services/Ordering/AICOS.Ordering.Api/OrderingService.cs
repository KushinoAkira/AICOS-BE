using AICOS.BuildingBlocks.Hosting;

namespace AICOS.Ordering.Api;

/// <summary>Identity of the Order Service. Also the assembly marker used by integration tests.</summary>
public sealed class OrderingService
{
    public static readonly AicosServiceInfo Info = new(
        Name: "order",
        DisplayName: "Order Service",
        RoutePrefix: "orders",
        Schema: "public");

    private OrderingService()
    {
    }
}
