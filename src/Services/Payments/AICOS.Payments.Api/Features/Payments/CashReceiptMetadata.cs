using System.Globalization;
using AICOS.Payments.Api.Domain;

namespace AICOS.Payments.Api.Features.Payments;

/// <summary>Read back tendered/change metadata written by the cash endpoint; legacy rows have unknown tendered.</summary>
internal static class CashReceiptMetadata
{
    public static decimal? Tendered(Payment payment)
    {
        if (!payment.IsCash || !payment.Description.StartsWith("cash:v1:", StringComparison.Ordinal)) return null;
        var parts = payment.Description.Split(':');
        if (parts.Length != 4 || parts[2].Length != 64) return null;
        if (parts[3] == "exact") return payment.Amount;
        return decimal.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var amount)
            && amount >= payment.Amount && amount <= 100_000_000m ? amount : null;
    }
}
