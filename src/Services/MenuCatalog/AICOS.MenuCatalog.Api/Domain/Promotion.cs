using AICOS.BuildingBlocks.Domain;

namespace AICOS.MenuCatalog.Api.Domain;

public enum DiscountType
{
    /// <summary>DiscountValue is a percentage 1-100.</summary>
    Percentage,

    /// <summary>DiscountValue is an amount of money taken off the price.</summary>
    FixedAmount,
}

public enum PromotionStatus
{
    Active,
    Inactive,
}

/// <summary>Promotional campaign on some products of one branch (UC-14). BR-26: start date must be before end date.</summary>
public sealed class Promotion : AuditableEntity<Guid>, IBranchScoped
{
    public const int NameMaxLength = 255;

    private readonly List<PromotionProduct> _products = [];

    private Promotion()
    {
    }

    public Promotion(
        Guid branchId, string name, string? description, DiscountType discountType, decimal discountValue,
        DateTime startDateUtc, DateTime endDateUtc, IEnumerable<Guid> productIds)
    {
        Id = Guid.NewGuid();
        BranchId = branchId;
        Status = PromotionStatus.Active;
        Update(name, description, discountType, discountValue, startDateUtc, endDateUtc, productIds);
    }

    public Guid BranchId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public DiscountType DiscountType { get; private set; }

    public decimal DiscountValue { get; private set; }

    public DateTime StartDateUtc { get; private set; }

    public DateTime EndDateUtc { get; private set; }

    public PromotionStatus Status { get; private set; }

    public IReadOnlyList<PromotionProduct> Products => _products;

    public bool IsRunning(DateTime nowUtc) => Status == PromotionStatus.Active && StartDateUtc <= nowUtc && nowUtc < EndDateUtc;

    public void Update(
        string name, string? description, DiscountType discountType, decimal discountValue,
        DateTime startDateUtc, DateTime endDateUtc, IEnumerable<Guid> productIds)
    {
        Name = name.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        DiscountType = discountType;
        DiscountValue = discountValue;
        StartDateUtc = startDateUtc;
        EndDateUtc = endDateUtc;
        _products.Clear();
        _products.AddRange(productIds.Distinct().Select(id => new PromotionProduct(Id, id)));
    }

    public void ChangeStatus(PromotionStatus status) => Status = status;

    /// <summary>Price after this promotion, never below 0.</summary>
    public decimal Apply(decimal price) => DiscountType switch
    {
        DiscountType.Percentage => Math.Round(price * (100 - DiscountValue) / 100, 0),
        _ => Math.Max(0, price - DiscountValue),
    };
}

public sealed class PromotionProduct
{
    private PromotionProduct()
    {
    }

    public PromotionProduct(Guid promotionId, Guid productId)
    {
        PromotionId = promotionId;
        ProductId = productId;
    }

    public Guid PromotionId { get; private set; }

    public Guid ProductId { get; private set; }
}
