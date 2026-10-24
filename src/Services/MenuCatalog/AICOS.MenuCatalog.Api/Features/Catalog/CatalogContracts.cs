namespace AICOS.MenuCatalog.Api.Features.Catalog;

public sealed record CatalogResponse(
    Guid BranchId,
    IReadOnlyList<CategoryResponse> Categories);

public sealed record CategoryResponse(
    Guid Id,
    string Name,
    int SortOrder,
    IReadOnlyList<ProductResponse> Products);

public sealed record ProductResponse(
    Guid Id,
    string Name,
    string? Description,
    string? ImageUrl,
    IReadOnlyList<VariantResponse> Variants,
    IReadOnlyList<OptionGroupResponse> OptionGroups);

public sealed record VariantResponse(Guid Id, string SizeName, decimal Price);

public sealed record OptionGroupResponse(
    Guid Id,
    string Name,
    int MinSelect,
    int MaxSelect,
    IReadOnlyList<OptionResponse> Options);

public sealed record OptionResponse(Guid Id, string Name, decimal ExtraPrice);
