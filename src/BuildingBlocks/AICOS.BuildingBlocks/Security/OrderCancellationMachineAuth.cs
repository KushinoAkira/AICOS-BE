using System.Security.Cryptography;
using System.Text;

namespace AICOS.BuildingBlocks.Security;

public static class OrderCancellationMachineAuth
{
    public const string Header = "X-Aicos-Order-Cancellation-Key";
    public static bool GuardEnabled(IConfiguration config) => config.GetValue<bool>("OrderCancellation:GuardEnabled");
    public static string Key(IConfiguration config) => config["OrderCancellation:MachineKey"] is { Length: >= 32 and <= 256 } key
        ? key : throw new InvalidOperationException("A separate order cancellation machine key is required.");
    public static bool Valid(HttpContext http, IConfiguration config)
    {
        if (!GuardEnabled(config)) return false;
        var incoming = http.Request.Headers[Header].ToString();
        return incoming.Length <= 256 && CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(incoming)), SHA256.HashData(Encoding.UTF8.GetBytes(Key(config))));
    }
}
