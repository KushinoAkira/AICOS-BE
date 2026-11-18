using AICOS.BuildingBlocks.Validation;
using AICOS.MenuCatalog.Api.Domain;

namespace AICOS.MenuCatalog.Api.Features.Products;

/// <summary>
/// One choice of an option group. <see cref="RequiresGroup"/> + <see cref="RequiresOption"/> name another option of the same
/// product that must be chosen too (BR-14), e.g. a topping that is only offered with the iced drink.
/// </summary>
public sealed record ProductOptionRequest(string? Name, decimal? PriceDelta, bool IsDefault, string? RequiresGroup = null, string? RequiresOption = null);

/// <summary><see cref="Kind"/> says what the group is (Size, Sugar, Ice, Topping, Other; default Other). Size, Sugar and Ice are single-choice.</summary>
public sealed record OptionGroupRequest(
    string? Name, OptionKind? Kind, SelectionType? SelectionType, bool IsRequired, int? MaxSelections, int? SortOrder, List<ProductOptionRequest>? Options);

/// <summary>Body of POST/PUT /products. The price is the brand base price; a branch may override it (PUT /products/{id}/branches/{branchId}).</summary>
public sealed record ProductRequest(
    Guid? CategoryId,
    string? Name,
    string? Description,
    string? ImageUrl,
    decimal? BasePrice,
    List<string>? Tags,
    List<OptionGroupRequest>? OptionGroups);

public sealed record ProductOptionResponse(Guid Id, string Name, decimal PriceDelta, bool IsDefault, Guid? RequiresOptionId);

public sealed record OptionGroupResponse(
    Guid Id, string Name, OptionKind Kind, SelectionType SelectionType, bool IsRequired, int? MaxSelections, int SortOrder, List<ProductOptionResponse> Options);

public sealed record BranchPriceResponse(Guid BranchId, decimal Price);

public sealed record ProductResponse(
    Guid Id,
    Guid CategoryId,
    string Name,
    string? Description,
    string? ImageUrl,
    decimal BasePrice,
    List<string> Tags,
    List<OptionGroupResponse> OptionGroups,
    DateTime CreatedAtUtc,
    DateTime? UpdatedAtUtc,
    List<BranchPriceResponse>? BranchPrices = null,
    List<Guid>? SoldOutBranchIds = null)
{
    public static ProductResponse From(Product p) => new(
        p.Id,
        p.CategoryId,
        p.Name,
        p.Description,
        p.ImageUrl,
        p.BasePrice,
        p.Tags,
        MapGroups(p),
        p.CreatedAtUtc,
        p.UpdatedAtUtc);

    public static List<OptionGroupResponse> MapGroups(Product p) => p.OptionGroups
        .OrderBy(g => g.SortOrder)
        .Select(g => new OptionGroupResponse(
            g.Id, g.Name, g.Kind, g.SelectionType, g.IsRequired, g.MaxSelections, g.SortOrder,
            g.Options.Select(o => new ProductOptionResponse(o.Id, o.Name, o.PriceDelta, o.IsDefault, o.RequiresOptionId)).ToList()))
        .ToList();
}

internal static class ProductValidator
{
    public const decimal MaxPrice = 100_000_000m;
    public const int MaxTags = 10;
    public const int MaxTagLength = 30;
    public const int MaxGroups = 10;
    public const int MaxOptionsPerGroup = 30;

    /// <summary>An uploaded drink photo is stored as a data URL; the web app shrinks it before sending.</summary>
    public const int MaxPictureLength = 400_000;

    private static bool IsPicture(string? value) =>
        value is not null && (value.StartsWith("data:image/jpeg;base64,", StringComparison.Ordinal) || value.StartsWith("data:image/png;base64,", StringComparison.Ordinal) || value.StartsWith("data:image/webp;base64,", StringComparison.Ordinal));

    public static void EnsureValid(ProductRequest r)
    {
        var errors = new ValidationErrors();
        CommonRules.RequiredId(errors, "CategoryId", r.CategoryId, "Category");
        CommonRules.Required(errors, "Name", r.Name, "Product name", Product.NameMaxLength);
        CommonRules.MaxLength(errors, "Description", r.Description, "Description", Product.DescriptionMaxLength);
        // A link, or a small picture uploaded from the computer (resized by the web app), kept in the record like the brand logo.
        if (IsPicture(r.ImageUrl))
        {
            if (r.ImageUrl!.Length > MaxPictureLength)
            {
                errors.Add("ImageUrl", $"The picture is too large (at most {MaxPictureLength / 1000} KB once encoded). Use a smaller image.");
            }
        }
        else
        {
            CommonRules.OptionalUrl(errors, "ImageUrl", r.ImageUrl, "Image URL");
        }
        CommonRules.Positive(errors, "BasePrice", r.BasePrice, "Base price", MaxPrice);

        var tags = r.Tags ?? [];
        if (tags.Count > MaxTags || tags.Any(t => string.IsNullOrWhiteSpace(t) || t.Trim().Length > MaxTagLength || t.Contains(',')))
        {
            errors.Add("Tags", $"At most {MaxTags} tags, each 1-{MaxTagLength} characters without commas.");
        }

        var groups = r.OptionGroups ?? [];
        if (groups.Count > MaxGroups)
        {
            errors.Add("OptionGroups", $"A product can have at most {MaxGroups} option groups.");
        }

        if (groups.Where(g => !string.IsNullOrWhiteSpace(g.Name)).GroupBy(g => g.Name!.Trim().ToLower()).Any(x => x.Count() > 1))
        {
            errors.Add("OptionGroups", "Option group names must be unique within a product.");
        }

        foreach (var kind in new[] { OptionKind.Size, OptionKind.Sugar, OptionKind.Ice })
        {
            if (groups.Count(g => g.Kind == kind) > 1)
            {
                errors.Add("OptionGroups", $"A product can have only one {kind} group.");
            }
        }

        for (var i = 0; i < groups.Count; i++)
        {
            ValidateGroup(errors, $"OptionGroups[{i}]", groups[i]);
        }

        if (!errors.HasAny)
        {
            ValidateDependencies(errors, groups);
        }

        errors.ThrowIfAny();
    }

    private static void ValidateGroup(ValidationErrors errors, string path, OptionGroupRequest g)
    {
        CommonRules.Required(errors, $"{path}.Name", g.Name, "Option group name", OptionGroup.NameMaxLength);
        CommonRules.RequiredEnum(errors, $"{path}.SelectionType", g.SelectionType, "Selection type");

        var kind = g.Kind ?? OptionKind.Other;
        if (kind is OptionKind.Size or OptionKind.Sugar or OptionKind.Ice && g.SelectionType == SelectionType.Multiple)
        {
            errors.Add($"{path}.SelectionType", $"A {kind} group is single-choice.");
        }

        var options = g.Options ?? [];
        if (options.Count is 0 or > MaxOptionsPerGroup)
        {
            errors.Add($"{path}.Options", $"An option group needs 1-{MaxOptionsPerGroup} options.");
        }

        if (g.MaxSelections is not null && (g.SelectionType != SelectionType.Multiple || g.MaxSelections < 1 || g.MaxSelections > Math.Max(options.Count, 1)))
        {
            errors.Add($"{path}.MaxSelections", "Max selections applies to multiple-choice groups and must be between 1 and the number of options.");
        }

        if (options.Where(o => !string.IsNullOrWhiteSpace(o.Name)).GroupBy(o => o.Name!.Trim().ToLower()).Any(x => x.Count() > 1))
        {
            errors.Add($"{path}.Options", "Option names must be unique within a group.");
        }

        if (g.SelectionType == SelectionType.Single && options.Count(o => o.IsDefault) > 1)
        {
            errors.Add($"{path}.Options", "A single-choice group can have at most one default option.");
        }

        for (var i = 0; i < options.Count; i++)
        {
            CommonRules.Required(errors, $"{path}.Options[{i}].Name", options[i].Name, "Option name", ProductOption.NameMaxLength);
            CommonRules.NonNegative(errors, $"{path}.Options[{i}].PriceDelta", options[i].PriceDelta ?? 0, "Price delta", 10_000_000m);
        }
    }

    /// <summary>BR-14: a dependency names an existing option of another group, and that option must not depend on something itself.</summary>
    private static void ValidateDependencies(ValidationErrors errors, List<OptionGroupRequest> groups)
    {
        var lookup = groups
            .SelectMany(g => (g.Options ?? []).Select(o => (Group: g, Option: o)))
            .ToDictionary(x => Key(x.Group.Name!, x.Option.Name!), x => x.Option);

        for (var gi = 0; gi < groups.Count; gi++)
        {
            var options = groups[gi].Options ?? [];
            for (var oi = 0; oi < options.Count; oi++)
            {
                var o = options[oi];
                var path = $"OptionGroups[{gi}].Options[{oi}]";
                var hasGroup = !string.IsNullOrWhiteSpace(o.RequiresGroup);
                var hasOption = !string.IsNullOrWhiteSpace(o.RequiresOption);
                if (!hasGroup && !hasOption)
                {
                    continue;
                }

                if (hasGroup != hasOption)
                {
                    errors.Add($"{path}.Requires", "Give both RequiresGroup and RequiresOption, or neither.");
                }
                else if (string.Equals(o.RequiresGroup!.Trim(), groups[gi].Name!.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"{path}.Requires", "An option cannot depend on an option of its own group.");
                }
                else if (!lookup.TryGetValue(Key(o.RequiresGroup, o.RequiresOption!), out var parent))
                {
                    errors.Add($"{path}.Requires", $"'{o.RequiresGroup}' / '{o.RequiresOption}' is not an option of this product.");
                }
                else if (!string.IsNullOrWhiteSpace(parent.RequiresOption))
                {
                    errors.Add($"{path}.Requires", "A dependency cannot depend on an option that itself depends on another.");
                }
            }
        }
    }

    public static IEnumerable<OptionGroup> ToEntities(List<OptionGroupRequest>? groups)
    {
        var requests = groups ?? [];
        var built = requests.Select((g, index) => new OptionGroup(
            g.Name!,
            g.Kind ?? OptionKind.Other,
            g.SelectionType!.Value,
            g.IsRequired,
            g.MaxSelections,
            g.SortOrder ?? index,
            g.Options!.Select(o => new ProductOption(o.Name!, o.PriceDelta ?? 0, o.IsDefault)))).ToList();

        // Second pass: dependencies refer to other options by name, which only have ids now.
        var byName = new Dictionary<string, ProductOption>();
        for (var gi = 0; gi < requests.Count; gi++)
        {
            for (var oi = 0; oi < built[gi].Options.Count; oi++)
            {
                byName[Key(requests[gi].Name!, requests[gi].Options![oi].Name!)] = built[gi].Options[oi];
            }
        }

        for (var gi = 0; gi < requests.Count; gi++)
        {
            for (var oi = 0; oi < built[gi].Options.Count; oi++)
            {
                var r = requests[gi].Options![oi];
                if (!string.IsNullOrWhiteSpace(r.RequiresGroup) && !string.IsNullOrWhiteSpace(r.RequiresOption))
                {
                    built[gi].Options[oi].RequireOption(byName[Key(r.RequiresGroup, r.RequiresOption)].Id);
                }
            }
        }

        return built;
    }

    private static string Key(string group, string option) => $"{group.Trim().ToLowerInvariant()}|{option.Trim().ToLowerInvariant()}";
}
