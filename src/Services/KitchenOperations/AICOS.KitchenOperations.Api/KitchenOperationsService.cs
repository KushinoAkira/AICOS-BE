using AICOS.BuildingBlocks.Hosting;

namespace AICOS.KitchenOperations.Api;

/// <summary>Identity of the Kitchen Operations Service. Also the assembly marker used by integration tests.</summary>
public sealed class KitchenOperationsService
{
    public static readonly AicosServiceInfo Info = new(
        Name: "kitchen",
        DisplayName: "Kitchen Operations Service",
        RoutePrefix: "kitchen",
        Schema: "kitchen");

    private KitchenOperationsService()
    {
    }
}
