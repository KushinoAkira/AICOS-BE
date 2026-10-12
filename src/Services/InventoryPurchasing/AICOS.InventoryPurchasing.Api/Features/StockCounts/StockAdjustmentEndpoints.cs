using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Persistence;
using AICOS.BuildingBlocks.Security;
using AICOS.BuildingBlocks.Validation;
using AICOS.InventoryPurchasing.Api.Domain;
using AICOS.InventoryPurchasing.Api.Features.Ingredients;
using AICOS.InventoryPurchasing.Api.Features.PurchaseOrders;
using AICOS.InventoryPurchasing.Api.Infrastructure;
using AICOS.InventoryPurchasing.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.InventoryPurchasing.Api.Features.StockCounts;

public sealed record StockCountLineRequest(Guid? IngredientId, decimal? CountedQuantity);

public sealed record StockCountRequest(Guid? BranchId, string? Note, List<StockCountLineRequest>? Lines);

public sealed record StockAdjustmentLineResponse(
    Guid IngredientId, string IngredientName, string Unit, decimal SystemQuantity, decimal CountedQuantity, decimal Difference);

public sealed record StockAdjustmentResponse(
    Guid Id, string Code, Guid BranchId, AdjustmentStatus Status, string? Note, string? RejectReason,
    Guid? CreatedBy, DateTime CreatedAtUtc, Guid? DecidedBy, DateTime? DecidedAtUtc, List<StockAdjustmentLineResponse> Lines);

/// <summary>
/// UC-21 Physical Inventory Count and Adjustment. Routes: /api/v1/inventory/stock-adjustments.
/// Warehouse submits counted quantities -> PendingApproval. BR-15: stock changes only when a Manager approves;
/// the DIFFERENCE recorded at count time is applied (sales made after the count are not lost).
/// </summary>
public static class StockAdjustmentEndpoints
{
    public static void MapStockAdjustmentEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/stock-adjustments").WithTags("Stock counts / adjustments");

        group.MapGet("/", ListAsync).RequireAuthorization(AicosPolicies.Warehouse);
        group.MapGet("/{id:guid}", GetAsync).RequireAuthorization(AicosPolicies.Warehouse).WithName("GetStockAdjustment");
        group.MapPost("/", CreateAsync).RequireAuthorization(AicosPolicies.Warehouse);
        group.MapPost("/{id:guid}/approve", ApproveAsync).RequireAuthorization(AicosPolicies.Management);
        group.MapPost("/{id:guid}/reject", RejectAsync).RequireAuthorization(AicosPolicies.Management);
    }

    private static async Task<PagedResponse<StockAdjustmentResponse>> ListAsync(
        InventoryPurchasingDbContext db, ICurrentUser user, Guid? branchId, AdjustmentStatus? status,
        int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        var branch = BranchAccess.ResolveFilter(user, branchId);
        var query = db.StockAdjustments.AsNoTracking().Include(a => a.Lines).AsQueryable();
        if (branch is not null)
        {
            query = query.Where(a => a.BranchId == branch);
        }

        if (status is not null)
        {
            query = query.Where(a => a.Status == status);
        }

        var paged = await query.OrderByDescending(a => a.CreatedAtUtc).ToPagedAsync(page, pageSize, a => a, ct);
        var items = new List<StockAdjustmentResponse>();
        foreach (var adjustment in paged.Items)
        {
            items.Add(await MapAsync(db, adjustment, ct));
        }

        return new PagedResponse<StockAdjustmentResponse>(items, paged.Page, paged.PageSize, paged.TotalCount);
    }

    private static async Task<StockAdjustmentResponse> GetAsync(Guid id, InventoryPurchasingDbContext db, ICurrentUser user, CancellationToken ct) =>
        await MapAsync(db, await FindAsync(db, user, id, ct), ct);

    private static async Task<IResult> CreateAsync(
        StockCountRequest r, InventoryPurchasingDbContext db, ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        CommonRules.MaxLength(errors, "Note", r.Note, "Note", 1000);
        var lines = r.Lines ?? [];
        if (lines.Count is 0 or > 200)
        {
            errors.Add("Lines", "A stock count needs 1-200 lines.");
        }

        if (lines.Where(l => l.IngredientId is not null).GroupBy(l => l.IngredientId).Any(g => g.Count() > 1))
        {
            errors.Add("Lines", "Each ingredient may appear only once per count.");
        }

        for (var i = 0; i < lines.Count; i++)
        {
            CommonRules.RequiredId(errors, $"Lines[{i}].IngredientId", lines[i].IngredientId, "Ingredient");
            CommonRules.NonNegative(errors, $"Lines[{i}].CountedQuantity", lines[i].CountedQuantity, "Counted quantity", IngredientEndpoints.MaxQuantity);
        }

        errors.ThrowIfAny();

        var branchId = BranchAccess.Resolve(user, r.BranchId);
        var ingredients = await IngredientEndpoints.LoadForBranchAsync(db, branchId, lines.Select(l => l.IngredientId!.Value), "Lines", ct);

        var adjustment = new StockAdjustment(
            branchId,
            StockLedger.NewDocumentNo("SC", clock.GetUtcNow().UtcDateTime),
            r.Note,
            lines.Select(l => new StockAdjustmentLine(l.IngredientId!.Value, ingredients[l.IngredientId!.Value].QuantityOnHand, l.CountedQuantity!.Value)));
        db.StockAdjustments.Add(adjustment);
        await db.SaveChangesAsync(ct);

        return Results.CreatedAtRoute("GetStockAdjustment", new { id = adjustment.Id }, await MapAsync(db, adjustment, ct));
    }

    private static async Task<StockAdjustmentResponse> ApproveAsync(
        Guid id, InventoryPurchasingDbContext db, ICurrentUser user, StockLedger ledger, TimeProvider clock, CancellationToken ct)
    {
        var adjustment = await FindAsync(db, user, id, ct);
        adjustment.Approve(user.UserId, clock.GetUtcNow().UtcDateTime);

        var ingredients = await IngredientEndpoints.LoadForBranchAsync(db, adjustment.BranchId, adjustment.Lines.Select(l => l.IngredientId), "Lines", ct);
        foreach (var line in adjustment.Lines.Where(l => l.Difference != 0))
        {
            ledger.Apply(ingredients[line.IngredientId], StockTransactionType.Adjustment, line.Difference, adjustment.Code, adjustment.Id, adjustment.Note);
        }

        await ledger.SaveAndPublishAsync(ct);
        return await MapAsync(db, adjustment, ct);
    }

    private static async Task<StockAdjustmentResponse> RejectAsync(
        Guid id, RejectRequest r, InventoryPurchasingDbContext db, ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        CommonRules.Required(errors, "Reason", r.Reason, "Reject reason", 500);
        errors.ThrowIfAny();

        var adjustment = await FindAsync(db, user, id, ct);
        adjustment.Reject(user.UserId, r.Reason!, clock.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct);
        return await MapAsync(db, adjustment, ct);
    }

    private static async Task<StockAdjustment> FindAsync(InventoryPurchasingDbContext db, ICurrentUser user, Guid id, CancellationToken ct)
    {
        var adjustment = await db.StockAdjustments.Include(a => a.Lines).FirstOrDefaultAsync(a => a.Id == id, ct)
            ?? throw new NotFoundException("Stock adjustment", id);
        BranchAccess.EnsureCanAccess(user, adjustment.BranchId);
        return adjustment;
    }

    private static async Task<StockAdjustmentResponse> MapAsync(InventoryPurchasingDbContext db, StockAdjustment a, CancellationToken ct)
    {
        var ids = a.Lines.Select(l => l.IngredientId).ToList();
        var ingredients = await db.Ingredients.AsNoTracking().Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        return new StockAdjustmentResponse(
            a.Id, a.Code, a.BranchId, a.Status, a.Note, a.RejectReason, a.CreatedBy, a.CreatedAtUtc, a.DecidedBy, a.DecidedAtUtc,
            a.Lines.Select(l => new StockAdjustmentLineResponse(
                l.IngredientId, ingredients[l.IngredientId].Name, ingredients[l.IngredientId].Unit, l.SystemQuantity, l.CountedQuantity, l.Difference))
                .ToList());
    }
}
