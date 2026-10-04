using AICOS.BuildingBlocks.Hosting;

namespace AICOS.Payments.Api;

/// <summary>Identity of the Payment Service. Also the assembly marker used by integration tests.</summary>
public sealed class PaymentsService
{
    public static readonly AicosServiceInfo Info = new(
        Name: "payment",
        DisplayName: "Payment Service",
        RoutePrefix: "payments",
        Schema: "payment");

    private PaymentsService()
    {
    }
}