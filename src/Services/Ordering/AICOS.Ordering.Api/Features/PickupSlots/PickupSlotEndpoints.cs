using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Security;
using AICOS.BuildingBlocks.Validation;
using AICOS.Ordering.Api.Domain;
using AICOS.Ordering.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.Ordering.Api.Features.PickupSlots;

/// <summary>PUT /pickup-slots: <c>BranchId</c> empty sets the brand default, otherwise the override of that branch.</summary>
public sealed record PickupSlotRequest(Guid? BranchId, int? SlotMinutes, int? CapacityPerSlot, int? CutoffMinutes, int? AdvanceDays);

public sealed record PickupSlotSettingResponse(Guid Id, Guid? BranchId, int SlotMinutes, int CapacityPerSlot, int CutoffMinutes, int AdvanceDays, DateTime? UpdatedAtUtc)
{
    public static PickupSlotSettingResponse From(PickupSlotSetting s) =>
        new(s.Id, s.BranchId, s.SlotMinutes, s.CapacityPerSlot, s.CutoffMinutes, s.AdvanceDays, s.UpdatedAtUtc ?? s.CreatedAtUtc);
}

/// <summary>
/// UC-21 Configure Pickup Slot Capacity and Cut-off. Routes: /api/v1/orders/pickup-slots. The BrandAdmin sets the brand default and any
/// branch; a BranchManager sets the override of their own branch. <c>GET /effective</c> tells any signed-in user the rules at a branch.
/// </summary>
public static class PickupSlotEndpoints
{
    public static void MapPickupSlotEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/pickup-slots").WithTags("Pickup slots");

        group.MapGet("/", ListAsync).RequireAuthorization(AicosPolicies.Management);
        group.MapGet("/effective", EffectiveAsync).RequireAuthorization();
        group.MapPut("/", UpsertAsync).RequireAuthorization(AicosPolicies.Management);
        group.MapDelete("/{id:guid}", DeleteAsync).RequireAuthorization(AicosPolicies.Management);
    }

    private static async Task<List<PickupSlotSettingResponse>> ListAsync(OrderingDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var tenantId = TenantScope.Require(user);
        var query = db.PickupSlotSettings.AsNoTracking().Where(s => s.TenantId == tenantId);
        if (!user.IsInRole(AicosRoles.BrandAdmin))
        {
            var own = user.BranchId ?? throw new ForbiddenException("Your account is not assigned to a branch.");
            query = query.Where(s => s.BranchId == null || s.BranchId == own);
        }

        return (await query.OrderBy(s => s.BranchId).ToListAsync(ct)).Select(PickupSlotSettingResponse.From).ToList();
    }

    /// <summary>The rules at a branch: its own override, else the brand default, else the platform defaults.</summary>
    private static async Task<EffectivePickupSlots> EffectiveAsync(OrderingDbContext db, ICurrentUser user, Guid? branchId, CancellationToken ct) =>
        await ForBranchAsync(db, TenantScope.Require(user), branchId ?? user.BranchId, ct);

    /// <summary>Used by the order flow to enforce capacity and cut-off.</summary>
    internal static async Task<EffectivePickupSlots> ForBranchAsync(OrderingDbContext db, Guid tenantId, Guid? branchId, CancellationToken ct)
    {
        var rows = await db.PickupSlotSettings.AsNoTracking()
            .Where(s => s.TenantId == tenantId && (s.BranchId == null || s.BranchId == branchId))
            .ToListAsync(ct);
        var own = branchId is null ? null : rows.FirstOrDefault(s => s.BranchId == branchId);
        if (own is not null)
        {
            return own.ToValues("Branch");
        }

        var brand = rows.FirstOrDefault(s => s.BranchId == null);
        return brand?.ToValues("Brand") ?? EffectivePickupSlots.Defaults;
    }

    private static async Task<IResult> UpsertAsync(
        PickupSlotRequest r, OrderingDbContext db, ICurrentUser user, IBranchDirectory directory, IEventBus bus, ILoggerFactory loggers, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        if (r.SlotMinutes is null or < 5 or > 120)
        {
            errors.Add("SlotMinutes", "A slot lasts 5-120 minutes.");
        }

        if (r.CapacityPerSlot is null or < 1 or > 500)
        {
            errors.Add("CapacityPerSlot", "A slot accepts 1-500 orders.");
        }

        if (r.CutoffMinutes is null or < 0 or > 240)
        {
            errors.Add("CutoffMinutes", "The cut-off is 0-240 minutes before the slot.");
        }

        if (r.AdvanceDays is null or < 0 or > 14)
        {
            errors.Add("AdvanceDays", "Customers can order 0-14 days ahead.");
        }

        errors.ThrowIfAny();

        var tenantId = TenantScope.Require(user);
        await EnsureCanManageAsync(user, directory, r.BranchId, requireActive: true, ct);

        var values = new EffectivePickupSlots(r.SlotMinutes!.Value, r.CapacityPerSlot!.Value, r.CutoffMinutes!.Value, r.AdvanceDays!.Value, "Branch");
        var setting = await db.PickupSlotSettings.FirstOrDefaultAsync(s => s.TenantId == tenantId && s.BranchId == r.BranchId, ct);
        var created = setting is null;
        var before = created ? null : Snapshot(setting!);
        if (setting is null)
        {
            setting = db.PickupSlotSettings.Add(new PickupSlotSetting(tenantId, r.BranchId, values)).Entity;
        }
        else
        {
            setting.Apply(values);
        }

        await db.SaveChangesAsync(ct);
        await AuditTrail.PublishAsync(
            bus, user, loggers.CreateLogger("AICOS.Ordering.PickupSlots"), tenantId, created ? "pickup_slot_setting.created" : "pickup_slot_setting.updated",
            "PickupSlotSetting", setting.Id, before, Snapshot(setting));

        return created
            ? Results.Created($"/api/v1/orders/pickup-slots/{setting.Id}", PickupSlotSettingResponse.From(setting))
            : Results.Ok(PickupSlotSettingResponse.From(setting));
    }

    /// <summary>Removing an override sends the branch back to the brand default (or the platform defaults).</summary>
    private static async Task<IResult> DeleteAsync(
        Guid id, OrderingDbContext db, ICurrentUser user, IBranchDirectory directory, IEventBus bus, ILoggerFactory loggers, CancellationToken ct)
    {
        var tenantId = TenantScope.Require(user);
        var setting = await db.PickupSlotSettings.FirstOrDefaultAsync(s => s.Id == id && s.TenantId == tenantId, ct) ?? throw new NotFoundException("Pickup slot setting", id);
        await EnsureCanManageAsync(user, directory, setting.BranchId, requireActive: false, ct);

        var before = Snapshot(setting);
        db.PickupSlotSettings.Remove(setting);
        await db.SaveChangesAsync(ct);
        await AuditTrail.PublishAsync(bus, user, loggers.CreateLogger("AICOS.Ordering.PickupSlots"), tenantId, "pickup_slot_setting.deleted", "PickupSlotSetting", id, before);
        return Results.NoContent();
    }

    /// <summary>The brand default belongs to the BrandAdmin; a branch row to the BrandAdmin or the manager of that branch.</summary>
    private static async Task EnsureCanManageAsync(ICurrentUser user, IBranchDirectory directory, Guid? branchId, bool requireActive, CancellationToken ct)
    {
        if (branchId is null)
        {
            if (!user.IsInRole(AicosRoles.BrandAdmin))
            {
                throw new ForbiddenException("Only the brand admin can change the brand default.");
            }

            return;
        }

        BranchAccess.EnsureCanAccess(user, branchId.Value);
        if (user.IsInRole(AicosRoles.BrandAdmin))
        {
            await BranchScope.EnsureUsableAsync(directory, branchId.Value, requireActive, ct);
        }
    }

    private static object Snapshot(PickupSlotSetting s) => new { s.BranchId, s.SlotMinutes, s.CapacityPerSlot, s.CutoffMinutes, s.AdvanceDays };
}
