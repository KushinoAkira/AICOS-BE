using AICOS.BuildingBlocks.Domain;

namespace AICOS.Ordering.Api.Domain;

/// <summary>
/// How takeaway pickup time slots work (UC-21, FR-17, BR-22, BR-23, BR-24). A brand-wide row (null <see cref="BranchId"/>) is the
/// default; a branch row replaces it for that branch. A brand that configured nothing uses <see cref="EffectivePickupSlots.Defaults"/>.
/// </summary>
public sealed class PickupSlotSetting : AuditableEntity<Guid>
{
    private PickupSlotSetting()
    {
    }

    public PickupSlotSetting(Guid tenantId, Guid? branchId, EffectivePickupSlots values)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        BranchId = branchId;
        Apply(values);
    }

    public Guid TenantId { get; private set; }

    /// <summary>Null means brand-wide.</summary>
    public Guid? BranchId { get; private set; }

    /// <summary>Length of one pickup slot in minutes.</summary>
    public int SlotMinutes { get; private set; }

    /// <summary>BR-22: orders one slot accepts; a full slot is no longer offered.</summary>
    public int CapacityPerSlot { get; private set; }

    /// <summary>BR-23 / BR-24: minutes before the slot starts after which it is locked: no new order, and the customer can no longer cancel.</summary>
    public int CutoffMinutes { get; private set; }

    /// <summary>How many days ahead a customer may pick a slot (0 = today only).</summary>
    public int AdvanceDays { get; private set; }

    public void Apply(EffectivePickupSlots v)
    {
        SlotMinutes = v.SlotMinutes;
        CapacityPerSlot = v.CapacityPerSlot;
        CutoffMinutes = v.CutoffMinutes;
        AdvanceDays = v.AdvanceDays;
    }

    public EffectivePickupSlots ToValues(string source) => new(SlotMinutes, CapacityPerSlot, CutoffMinutes, AdvanceDays, source);
}

/// <summary>The pickup slot rules in force at a branch. <see cref="Source"/> is Branch, Brand or Default.</summary>
public sealed record EffectivePickupSlots(int SlotMinutes, int CapacityPerSlot, int CutoffMinutes, int AdvanceDays, string Source)
{
    public static EffectivePickupSlots Defaults { get; } = new(15, 10, 15, 1, "Default");
}
