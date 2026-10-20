using AICOS.BuildingBlocks.Domain;

namespace AICOS.MenuCatalog.Api.Domain;

public enum CategoryStatus
{
    Active,

    /// <summary>Hidden from every branch menu together with all of its products.</summary>
    Hidden,
}

/// <summary>Menu category of a brand (UC-14), e.g. "Cà phê", "Trà sữa". Shared by all branches of the tenant.</summary>
public sealed class Category : AuditableEntity<Guid>
{
    public const int NameMaxLength = 100;

    private Category()
    {
    }

    public Category(Guid tenantId, string name, int sortOrder)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        Status = CategoryStatus.Active;
        Update(name, sortOrder);
    }

    public Guid TenantId { get; private set; }

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

/// <summary>What an option group stands for, so the apps can show it properly and the order can be validated.</summary>
public enum OptionKind
{
    Size,
    Sugar,
    Ice,
    Topping,
    Other,
}

/// <summary>
/// A sellable menu item of a brand (UC-14). Owns its option groups (size, sugar, ice, toppings). The price here is the
/// base price; a branch may override it (BR-15) and decides whether it sells the item and whether it is sold out (see <see cref="BranchProduct"/>).
/// </summary>
public sealed class Product : AuditableEntity<Guid>
{
    public const int NameMaxLength = 255;
    public const int DescriptionMaxLength = 2000;

    private readonly List<OptionGroup> _optionGroups = [];

    private Product()
    {
    }

    public Product(Guid tenantId, Guid categoryId, string name, string? description, string? imageUrl, decimal basePrice, IEnumerable<string> tags)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        Update(categoryId, name, description, imageUrl, basePrice, tags);
    }

    public Guid TenantId { get; private set; }

    public Guid CategoryId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public string? ImageUrl { get; private set; }

    public decimal BasePrice { get; private set; }

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

    public void MarkDeleted(DateTime nowUtc) => DeletedAtUtc = nowUtc;
}

public sealed class OptionGroup : Entity<Guid>
{
    public const int NameMaxLength = 100;

    private readonly List<ProductOption> _options = [];

    private OptionGroup()
    {
    }

    public OptionGroup(
        string name, OptionKind kind, SelectionType selectionType, bool isRequired, int? maxSelections, int sortOrder, IEnumerable<ProductOption> options)
    {
        Id = Guid.NewGuid();
        Name = name.Trim();
        Kind = kind;
        SelectionType = selectionType;
        IsRequired = isRequired;
        MaxSelections = selectionType == SelectionType.Multiple ? maxSelections : null;
        SortOrder = sortOrder;
        _options.AddRange(options);
    }

    public Guid ProductId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public OptionKind Kind { get; private set; }

    public SelectionType SelectionType { get; private set; }

    public bool IsRequired { get; private set; }

    /// <summary>For multiple-choice groups: the most options a customer may pick (e.g. 5 toppings). Null means no limit.</summary>
    public int? MaxSelections { get; private set; }

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

    /// <summary>Added to the product price when chosen (e.g. size L +5,000). Never negative (BR-13).</summary>
    public decimal PriceDelta { get; private set; }

    public bool IsDefault { get; private set; }

    /// <summary>BR-14: this option can only be chosen together with that option (e.g. "pearl" topping needs "iced"). Same product.</summary>
    public Guid? RequiresOptionId { get; private set; }

    public void RequireOption(Guid optionId) => RequiresOptionId = optionId;
}

/// <summary>
/// What one branch does with one product (UC-15, UC-24): whether it sells it, whether it is sold out right now, and its own
/// price when it differs from the brand base price (BR-15). The brand decides publication and price; the branch decides availability.
/// </summary>
public sealed class BranchProduct : AuditableEntity<Guid>, IBranchScoped
{
    private BranchProduct()
    {
    }

    public BranchProduct(Guid tenantId, Guid branchId, Guid productId)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        BranchId = branchId;
        ProductId = productId;
        IsAvailable = true;
    }

    public Guid TenantId { get; private set; }

    public Guid BranchId { get; private set; }

    public Guid ProductId { get; private set; }

    /// <summary>The product is on this branch menu. Unpublished products cannot be ordered there (BR-16).</summary>
    public bool IsPublished { get; private set; }

    /// <summary>False while the branch has run out of the product (Sold Out). Reset to true when the product is published again.</summary>
    public bool IsAvailable { get; private set; }

    /// <summary>The price at this branch instead of the base price. Null means use the base price.</summary>
    public decimal? PriceOverride { get; private set; }

    public void Publish()
    {
        if (!IsPublished)
        {
            IsPublished = true;
            IsAvailable = true;
        }
    }

    public void Unpublish() => IsPublished = false;

    public void SetAvailability(bool isAvailable) => IsAvailable = isAvailable;

    public void SetPriceOverride(decimal? price) => PriceOverride = price;
}
