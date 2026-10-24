using AICOS.BuildingBlocks.Domain;

namespace AICOS.Ordering.Api.Domain.Entities;

/// <summary>An option the customer chose for one order line (size L, less ice, a topping), copied from the menu at the time of the order so later menu edits do not change old orders.</summary>
public class OrderLineOption : Entity<Guid>
{
    protected OrderLineOption()
    {
    }

    public OrderLineOption(Guid optionId, string groupName, string name, decimal priceDelta)
    {
        Id = Guid.NewGuid();
        OptionId = optionId;
        GroupName = groupName;
        Name = name;
        PriceDelta = priceDelta;
    }

    public Guid OrderItemId { get; private set; }

    public Guid OptionId { get; private set; }

    public string GroupName { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    /// <summary>The surcharge for one unit of the line (BR-13: never negative).</summary>
    public decimal PriceDelta { get; private set; }
}
