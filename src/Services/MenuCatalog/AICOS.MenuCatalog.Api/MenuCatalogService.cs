using AICOS.BuildingBlocks.Hosting;

namespace AICOS.MenuCatalog.Api;

/// <summary>Identity of the Menu & Catalog Service. Also the assembly marker used by integration tests.</summary>
public sealed class MenuCatalogService
{
    public static readonly AicosServiceInfo Info = new(
        Name: "menu",
        DisplayName: "Menu & Catalog Service",
        RoutePrefix: "menu",
        Schema: "menu");

    private MenuCatalogService()
    {
    }
}