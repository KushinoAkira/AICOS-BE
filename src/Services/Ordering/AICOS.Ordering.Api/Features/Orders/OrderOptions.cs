using AICOS.BuildingBlocks.Exceptions;
using AICOS.Ordering.Api.Domain.Entities;
using AICOS.Ordering.Api.Services;

namespace AICOS.Ordering.Api.Features.Orders;

/// <summary>
/// Checks the options a customer picked for a product against the menu and prices them (BR-13, BR-14): size and topping surcharges are added
/// to the unit price, a required group must be answered (its default is used when the customer sent nothing), a single-choice group takes
/// one option, a multiple-choice group at most its limit, and an option that needs another option must come with it.
/// </summary>
public static class OrderOptions
{
    public sealed record Priced(IReadOnlyList<OrderLineOption> Options, decimal Surcharge);

    public static Priced Resolve(MenuProductInfo product, IReadOnlyCollection<Guid>? optionIds)
    {
        var groups = product.OptionGroups ?? [];
        var picked = (optionIds ?? []).Distinct().ToList();
        var byId = groups.SelectMany(g => g.Options.Select(o => (Group: g, Option: o))).ToDictionary(x => x.Option.Id);

        var unknown = picked.Where(id => !byId.ContainsKey(id)).ToList();
        if (unknown.Count > 0)
        {
            throw new BusinessRuleException("order.option_invalid", $"These options do not belong to '{product.Name}': {string.Join(", ", unknown)}.");
        }

        var chosen = new List<(MenuOptionGroupInfo Group, MenuOptionInfo Option)>();
        foreach (var group in groups)
        {
            var inGroup = group.Options.Where(o => picked.Contains(o.Id)).ToList();
            if (inGroup.Count == 0 && group.IsRequired)
            {
                inGroup = group.Options.Where(o => o.IsDefault).Take(group.IsMultiple ? int.MaxValue : 1).ToList();
                if (inGroup.Count == 0)
                {
                    throw new BusinessRuleException("order.option_required", $"Choose an option for '{group.Name}' of '{product.Name}'.");
                }
            }

            if (!group.IsMultiple && inGroup.Count > 1)
            {
                throw new BusinessRuleException("order.option_too_many", $"Choose only one option for '{group.Name}' of '{product.Name}'.");
            }

            if (group.IsMultiple && group.MaxSelections is { } max && inGroup.Count > max)
            {
                throw new BusinessRuleException("order.option_too_many", $"Choose at most {max} option(s) for '{group.Name}' of '{product.Name}'.");
            }

            chosen.AddRange(inGroup.Select(o => (group, o)));
        }

        var chosenIds = chosen.Select(c => c.Option.Id).ToHashSet();
        foreach (var (group, option) in chosen)
        {
            if (option.RequiresOptionId is { } needed && !chosenIds.Contains(needed))
            {
                var neededName = byId.TryGetValue(needed, out var n) ? n.Option.Name : needed.ToString();
                throw new BusinessRuleException("order.option_requires", $"'{option.Name}' can only be chosen together with '{neededName}'.");
            }
        }

        var options = chosen.Select(c => new OrderLineOption(c.Option.Id, c.Group.Name, c.Option.Name, Math.Max(0, c.Option.PriceDelta))).ToList();
        return new Priced(options, options.Sum(o => o.PriceDelta));
    }
}
