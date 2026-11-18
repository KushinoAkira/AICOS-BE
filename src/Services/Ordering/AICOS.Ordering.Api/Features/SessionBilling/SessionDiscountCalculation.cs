using AICOS.BuildingBlocks.Exceptions;
using AICOS.Ordering.Api.Domain.Entities;
using System.Numerics;

namespace AICOS.Ordering.Api.Features.SessionBilling;

public sealed record DiscountAllocation(Guid OrderId, decimal Before, decimal After);

public static class SessionDiscountCalculation
{
    public static IReadOnlyList<DiscountAllocation> Allocate(IReadOnlyList<Order> orders, string mode, decimal value)
    {
        if (mode is not ("AMOUNT" or "PERCENT") || value < 0 || decimal.Round(value, 2) != value
            || mode == "AMOUNT" && decimal.Truncate(value) != value)
            throw new ValidationFailedException("value", "Use a non-negative whole VND amount or percentage with at most two decimals.");
        var sorted = orders.OrderBy(x => x.Id).ToArray();
        if (sorted.Any(x => x.OriginalAmount < 0 || decimal.Truncate(x.OriginalAmount) != x.OriginalAmount))
            throw new ConflictException("Whole VND price snapshots are required.", "discount.reconciliation");
        var subtotal = sorted.Sum(x => x.OriginalAmount);
        if (subtotal <= 0 || mode == "PERCENT" && value >= 100)
            throw new ConflictException("A positive amount must remain payable.", "discount.zero_total");
        var wholeSubtotal = new BigInteger(subtotal);
        var discount = mode == "PERCENT" ? wholeSubtotal * new BigInteger(value * 100) / 10000 : new BigInteger(value);
        if (discount >= wholeSubtotal) throw new ConflictException("A positive amount must remain payable.", "discount.zero_total");
        // Largest remainder allocation keeps each order non-negative and conserves whole VND exactly.
        var products = sorted.Select(x => discount * new BigInteger(x.OriginalAmount)).ToArray();
        var amounts = products.Select(x => x / wholeSubtotal).ToArray();
        var remainder = (int)(discount - amounts.Aggregate(BigInteger.Zero, (sum, x) => sum + x));
        foreach (var index in Enumerable.Range(0, sorted.Length)
                     .OrderByDescending(i => products[i] % wholeSubtotal).ThenBy(i => sorted[i].Id).Take(remainder))
            amounts[index]++;
        return sorted.Select((x, i) => new DiscountAllocation(x.Id, x.DiscountAmount, (decimal)amounts[i])).ToArray();
    }
}
