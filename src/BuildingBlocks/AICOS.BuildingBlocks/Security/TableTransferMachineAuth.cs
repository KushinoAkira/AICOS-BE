using System.Security.Cryptography;
using System.Text;
namespace AICOS.BuildingBlocks.Security;
public static class TableTransferMachineAuth
{
    public const string Header="X-Aicos-Table-Transfer-Key";
    public static string Key(IConfiguration c)=>c["TableTransfer:MachineKey"] is {Length:>=32} k?k:throw new InvalidOperationException("Configure separate TableTransfer machine key.");
    public static bool Valid(HttpContext h,IConfiguration c)=>c.GetValue<bool>("TableTransfer:Enabled")&&c["TableTransfer:MachineKey"] is {Length:>=32} k&&h.Request.Headers.TryGetValue(Header,out var s)&&CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(k),Encoding.UTF8.GetBytes(s.ToString()));
}
