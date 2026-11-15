using System.Security.Cryptography;
using System.Text;
namespace AICOS.BuildingBlocks.Security;

/// <summary>Separate machine credential; never a user JWT or database password.</summary>
public static class BillingMachineAuth
{
    public const string Header = "X-Aicos-Billing-Key";
    public static bool Enabled(IConfiguration config) => config.GetValue<bool>("SessionBilling:Enabled");
    public static string Key(IConfiguration config)
    {
        var key = config["SessionBilling:MachineKey"];
        if (string.IsNullOrWhiteSpace(key) || key.Length < 32 || key.Any(char.IsControl)) throw new InvalidOperationException("Configure a separate SessionBilling machine key (at least 32 characters).");
        return key;
    }
    public static bool Valid(HttpContext http, IConfiguration config)
    {
        if (!Enabled(config)) return false;
        var configured = config["SessionBilling:MachineKey"];
        return configured is { Length: >= 32 } && http.Request.Headers.TryGetValue(Header, out var supplied)
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(configured), Encoding.UTF8.GetBytes(supplied.ToString()));
    }
}
