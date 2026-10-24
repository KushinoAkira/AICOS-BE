namespace AICOS.MenuCatalog.Api.Domain;

public sealed class Category
{
    private Category()
    {
    }
    public Category(Guid id, Guid tenantId, string name, int sortOrder = 0)
    {
        Id = id;
        TenantId = tenantId;
        Name = name.Trim();
        SortOrder = sortOrder;
        IsActive = true;
    }
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public int? SortOrder { get; private set; }
    public bool? IsActive { get; private set; }
    public ICollection<Product> Products { get; private set; } = [];
}

public sealed class Product
{
    private Product()
    {
    }
    public Product(Guid id, Guid tenantId, Guid categoryId, string name)
    {
        Id = id;
        TenantId = tenantId;
        CategoryId = categoryId;
        Name = name.Trim();
        IsActive = true;
    }
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid CategoryId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public string? ImageUrl { get; private set; }
    public bool? IsActive { get; private set; }
    public DateTime? CreatedAt { get; private set; }
    public Category Category { get; private set; } = null!;
    public ICollection<ProductVariant> Variants { get; private set; } = [];
    public ICollection<ProductOptionGroup> OptionGroups { get; private set; } = [];
    public ICollection<BranchProduct> BranchProducts { get; private set; } = [];
}

public sealed class ProductVariant
{
    private ProductVariant()
    {
    }
    public ProductVariant(Guid id, Guid productId, string sizeName, decimal price)
    {
        Id = id;
        ProductId = productId;
        SizeName = sizeName.Trim();
        Price = price;
        IsActive = true;
    }
    public Guid Id { get; private set; }
    public Guid ProductId { get; private set; }
    public string SizeName { get; private set; } = string.Empty;
    public decimal Price { get; private set; }
    public bool? IsActive { get; private set; }
    public Product Product { get; private set; } = null!;
}

public sealed class ProductOptionGroup
{
    private ProductOptionGroup()
    {
    }
    public ProductOptionGroup(Guid id, Guid productId, string name, int minSelect = 0, int maxSelect = 1)
    {
        Id = id;
        ProductId = productId;
        Name = name.Trim();
        MinSelect = minSelect;
        MaxSelect = maxSelect;
    }
    public Guid Id { get; private set; }
    public Guid ProductId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public int? MinSelect { get; private set; }
    public int? MaxSelect { get; private set; }
    public Product Product { get; private set; } = null!;
    public ICollection<ProductOption> Options { get; private set; } = [];
}

public sealed class ProductOption
{
    private ProductOption()
    {
    }
    public ProductOption(Guid id, Guid groupId, string name, decimal extraPrice = 0)
    {
        Id = id;
        GroupId = groupId;
        Name = name.Trim();
        ExtraPrice = extraPrice;
        IsActive = true;
    }
    public Guid Id { get; private set; }
    public Guid GroupId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public decimal? ExtraPrice { get; private set; }
    public bool? IsActive { get; private set; }
    public ProductOptionGroup Group { get; private set; } = null!;
}

public sealed class BranchProduct
{
    private BranchProduct()
    {
    }
    public BranchProduct(Guid id, Guid branchId, Guid productId, bool isAvailable = true)
    {
        Id = id;
        BranchId = branchId;
        ProductId = productId;
        IsAvailable = isAvailable;
    }
    public Guid Id { get; private set; }
    public Guid BranchId { get; private set; }
    public Guid ProductId { get; private set; }
    public bool? IsAvailable { get; private set; }
    public Product Product { get; private set; } = null!;
}
