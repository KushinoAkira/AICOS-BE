using AICOS.BuildingBlocks.Hosting;

namespace AICOS.InventoryPurchasing.Api;

/// <summary>Identity of the Inventory & Purchasing Service. Also the assembly marker used by integration tests.</summary>
public sealed class InventoryPurchasingService
{
    public static readonly AicosServiceInfo Info = new(
        Name: "inventory",
        DisplayName: "Inventory & Purchasing Service",
        RoutePrefix: "inventory",
        Schema: "inventory");

    private InventoryPurchasingService()
    {
    }
}