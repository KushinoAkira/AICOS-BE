using AICOS.BuildingBlocks.Validation;
using AICOS.MenuCatalog.Api.Domain;

namespace AICOS.MenuCatalog.Api.Features.Products;

public sealed record ProductOptionRequest(string? Name, decimal? PriceDelta, bool IsDefault);

public sealed record OptionGroupRequest(string? Name, SelectionType? SelectionType, bool IsRequired, int? SortOrder, List<ProductOptionRequest>? Options);

/// <summary>Body of POST/PUT /products. <see cref="BranchId"/> is only read for a BrandAdmin.</summary>
public sealed record ProductRequest(
    Guid? BranchId,
    Guid? CategoryId,
    string? Name,
    string? Description,
    string? ImageUrl,
    decimal? BasePrice,
    List<string>? Tags,
    List<OptionGroupRequest>? OptionGroups);

public sealed record ChangeAvailabilityRequest(bool? IsAvailable);

public sealed record ProductOptionResponse(Guid Id, string Name, decimal PriceDelta, bool IsDefault);

public sealed record OptionGroupResponse(Guid Id, string Name, SelectionType SelectionType, bool IsRequired, int SortOrder, List<ProductOptionResponse> Options);

public sealed record ProductResponse(
    Guid Id,
    Guid BranchId,
    Guid CategoryId,
    string Name,
    string? Description,
    string? ImageUrl,
    decimal BasePrice,
    bool IsAvailable,
    List<string> Tags,
    List<OptionGroupResponse> OptionGroups,
    DateTime CreatedAtUtc,
    DateTime? UpdatedAtUtc)
{
    public static ProductResponse From(Product p) => new(
        p.Id,
        p.BranchId,
        p.CategoryId,
        p.Name,
        p.Description,
        p.ImageUrl,
        p.BasePrice,
        p.IsAvailable,
        p.Tags,
        MapGroups(p),
        p.CreatedAtUtc,
        p.UpdatedAtUtc);

    public static List<OptionGroupResponse> MapGroups(Product p) => p.OptionGroups
        .OrderBy(g => g.SortOrder)
        .Select(g => new OptionGroupResponse(
            g.Id, g.Name, g.SelectionType, g.IsRequired, g.SortOrder,
            g.Options.Select(o => new ProductOptionResponse(o.Id, o.Name, o.PriceDelta, o.IsDefault)).ToList()))
        .ToList();
}

internal static class ProductValidator
{
    public const decimal MaxPrice = 100_000_000m;
    public const int MaxTags = 10;
    public const int MaxTagLength = 30;
    public const int MaxGroups = 10;
    public const int MaxOptionsPerGroup = 30;

    public static void EnsureValid(ProductRequest r)
    {
        var errors = new ValidationErrors();
        CommonRules.RequiredId(errors, "CategoryId", r.CategoryId, "Category");
        CommonRules.Required(errors, "Name", r.Name, "Product name", Product.NameMaxLength);
        CommonRules.MaxLength(errors, "Description", r.Description, "Description", Product.DescriptionMaxLength);
        CommonRules.OptionalUrl(errors, "ImageUrl", r.ImageUrl, "Image URL");
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

        for (var i = 0; i < groups.Count; i++)
        {
            ValidateGroup(errors, $"OptionGroups[{i}]", groups[i]);
        }

        errors.ThrowIfAny();
    }

    private static void ValidateGroup(ValidationErrors errors, string path, OptionGroupRequest g)
    {
        CommonRules.Required(errors, $"{path}.Name", g.Name, "Option group name", OptionGroup.NameMaxLength);
        CommonRules.RequiredEnum(errors, $"{path}.SelectionType", g.SelectionType, "Selection type");

        var options = g.Options ?? [];
        if (options.Count is 0 or > MaxOptionsPerGroup)
        {
            errors.Add($"{path}.Options", $"An option group needs 1-{MaxOptionsPerGroup} options.");
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

    public static IEnumerable<OptionGroup> ToEntities(List<OptionGroupRequest>? groups) =>
        (groups ?? []).Select((g, index) => new OptionGroup(
            g.Name!,
            g.SelectionType!.Value,
            g.IsRequired,
            g.SortOrder ?? index,
            g.Options!.Select(o => new ProductOption(o.Name!, o.PriceDelta ?? 0, o.IsDefault))));
}
