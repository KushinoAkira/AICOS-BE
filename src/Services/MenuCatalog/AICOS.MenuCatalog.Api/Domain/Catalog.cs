using AICOS.BuildingBlocks.Domain;

namespace AICOS.MenuCatalog.Api.Domain;

public enum CategoryStatus
{
    Active,

    /// <summary>Hidden from the public menu together with all of its products.</summary>
    Hidden,
}

/// <summary>Menu category of one branch (UC-06), e.g. "Cà phê", "Trà sữa".</summary>
public sealed class Category : AuditableEntity<Guid>, IBranchScoped
{
    public const int NameMaxLength = 100;

    private Category()
    {
    }

    public Category(Guid branchId, string name, int sortOrder)
    {
        Id = Guid.NewGuid();
        BranchId = branchId;
        Status = CategoryStatus.Active;
        Update(name, sortOrder);
    }

    public Guid BranchId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public int SortOrder { get; private set; }

    public CategoryStatus Status { get; private set; }

    public void Update(string name, int sortOrder)
    {
        Name = name.Trim();
        SortOrder = sortOrder;
    }

    public void ChangeStatus(CategoryStatus status) => Status = status;
}

public enum SelectionType
{
    /// <summary>Pick exactly one (size, sugar level...).</summary>
    Single,

    /// <summary>Pick any number (toppings...).</summary>
    Multiple,
}

/// <summary>A sellable menu item (UC-06). Owns its option groups (size, sugar, toppings).</summary>
public sealed class Product : AuditableEntity<Guid>, IBranchScoped
{
    public const int NameMaxLength = 255;
    public const int DescriptionMaxLength = 2000;

    private readonly List<OptionGroup> _optionGroups = [];

    private Product()
    {
    }

    public Product(Guid branchId, Guid categoryId, string name, string? description, string? imageUrl, decimal basePrice, IEnumerable<string> tags)
    {
        Id = Guid.NewGuid();
        BranchId = branchId;
        IsAvailable = true;
        Update(categoryId, name, description, imageUrl, basePrice, tags);
    }

    public Guid BranchId { get; private set; }

    public Guid CategoryId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public string? ImageUrl { get; private set; }

    public decimal BasePrice { get; private set; }

    public bool IsAvailable { get; private set; }

    public List<string> Tags { get; private set; } = [];

    public IReadOnlyList<OptionGroup> OptionGroups => _optionGroups;

    public DateTime? DeletedAtUtc { get; private set; }

    public void Update(Guid categoryId, string name, string? description, string? imageUrl, decimal basePrice, IEnumerable<string> tags)
    {
        CategoryId = categoryId;
        Name = name.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        ImageUrl = string.IsNullOrWhiteSpace(imageUrl) ? null : imageUrl.Trim();
        BasePrice = basePrice;
        Tags = tags.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The client always sends the full list; groups not in it are deleted.</summary>
    public void ReplaceOptionGroups(IEnumerable<OptionGroup> groups)
    {
        _optionGroups.Clear();
        _optionGroups.AddRange(groups);
    }

    public void SetAvailability(bool isAvailable) => IsAvailable = isAvailable;

    public void MarkDeleted(DateTime nowUtc)
    {
        DeletedAtUtc = nowUtc;
        IsAvailable = false;
    }
}

public sealed class OptionGroup : Entity<Guid>
{
    public const int NameMaxLength = 100;

    private readonly List<ProductOption> _options = [];

    private OptionGroup()
    {
    }

    public OptionGroup(string name, SelectionType selectionType, bool isRequired, int sortOrder, IEnumerable<ProductOption> options)
    {
        Id = Guid.NewGuid();
        Name = name.Trim();
        SelectionType = selectionType;
        IsRequired = isRequired;
        SortOrder = sortOrder;
        _options.AddRange(options);
    }

    public Guid ProductId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public SelectionType SelectionType { get; private set; }

    public bool IsRequired { get; private set; }

    public int SortOrder { get; private set; }

    public IReadOnlyList<ProductOption> Options => _options;
}

public sealed class ProductOption : Entity<Guid>
{
    public const int NameMaxLength = 100;

    private ProductOption()
    {
    }

    public ProductOption(string name, decimal priceDelta, bool isDefault)
    {
        Id = Guid.NewGuid();
        Name = name.Trim();
        PriceDelta = priceDelta;
        IsDefault = isDefault;
    }

    public Guid OptionGroupId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    /// <summary>Added to the product's base price when chosen (e.g. size L +5,000).</summary>
    public decimal PriceDelta { get; private set; }

    public bool IsDefault { get; private set; }
}
