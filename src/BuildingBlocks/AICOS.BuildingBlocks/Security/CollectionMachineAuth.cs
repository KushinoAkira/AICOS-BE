using System.Security.Cryptography;
using System.Text;
namespace AICOS.BuildingBlocks.Security;
public static class CollectionMachineAuth
{
    public const string Header="X-Aicos-Collections-Key";
    public static bool Valid(HttpContext http,IConfiguration config)
    {
        var key=config["CashierShifts:MachineKey"];
        return config.GetValue<bool>("CashierShifts:Enabled") && key is {Length:>=32} && http.Request.Headers.TryGetValue(Header,out var supplied)
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(key),Encoding.UTF8.GetBytes(supplied.ToString()));
    }
}
