using AICOS.BuildingBlocks.Hosting;

namespace AICOS.TableReservation.Api;

/// <summary>Identity of the Table & Reservation Service. Also the assembly marker used by integration tests.</summary>
public sealed class TableReservationService
{
    public static readonly AicosServiceInfo Info = new(
        Name: "table",
        DisplayName: "Table & Reservation Service",
        RoutePrefix: "tables",
        Schema: "table_reservation");

    private TableReservationService()
    {
    }
}