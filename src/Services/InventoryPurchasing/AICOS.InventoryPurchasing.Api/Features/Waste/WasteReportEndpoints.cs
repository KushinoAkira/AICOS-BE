using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Persistence;
using AICOS.BuildingBlocks.Security;
using AICOS.BuildingBlocks.Validation;
using AICOS.InventoryPurchasing.Api.Domain;
using AICOS.InventoryPurchasing.Api.Features.Ingredients;
using AICOS.InventoryPurchasing.Api.Infrastructure;
using AICOS.InventoryPurchasing.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.InventoryPurchasing.Api.Features.Waste;

public sealed record WasteReportRequest(Guid? BranchId, Guid? IngredientId, decimal? Quantity, string? Reason, string? EvidenceImageUrl);

public sealed record WasteReportResponse(
    Guid Id, string Code, Guid BranchId, Guid IngredientId, string IngredientName, string Unit, decimal Quantity,
    decimal QuantityOnHandAfter, string Reason, string? EvidenceImageUrl, Guid? CreatedBy, DateTime CreatedAtUtc);

/// <summary>
/// UC-35 Report Ingredient Waste/Spoilage. Routes: /api/v1/inventory/waste-reports. Warehouse roles.
/// Stock is deducted immediately; BR-31: the wasted quantity cannot exceed what is in stock (422).
/// </summary>
public static class WasteReportEndpoints
{
    public static void MapWasteReportEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/waste-reports").WithTags("Waste reports").RequireAuthorization(AicosPolicies.Warehouse);

        group.MapGet("/", ListAsync);
        group.MapGet("/{id:guid}", GetAsync).WithName("GetWasteReport");
        group.MapPost("/", CreateAsync);
    }

    private static async Task<PagedResponse<WasteReportResponse>> ListAsync(
        InventoryPurchasingDbContext db, ICurrentUser user, Guid? branchId, Guid? ingredientId,
        int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        var branch = BranchAccess.ResolveFilter(user, branchId);
        var query = db.WasteReports.AsNoTracking();
        if (branch is not null)
        {
            query = query.Where(w => w.BranchId == branch);
        }

        if (ingredientId is not null)
        {
            query = query.Where(w => w.IngredientId == ingredientId);
        }

        var paged = await query.OrderByDescending(w => w.CreatedAtUtc).ToPagedAsync(page, pageSize, w => w, ct);
        var items = await MapAsync(db, paged.Items, ct);
        return new PagedResponse<WasteReportResponse>(items, paged.Page, paged.PageSize, paged.TotalCount);
    }

    private static async Task<WasteReportResponse> GetAsync(Guid id, InventoryPurchasingDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var report = await db.WasteReports.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct) ?? throw new NotFoundException("Waste report", id);
        BranchAccess.EnsureCanAccess(user, report.BranchId);
        return (await MapAsync(db, [report], ct))[0];
    }

    private static async Task<IResult> CreateAsync(
        WasteReportRequest r, InventoryPurchasingDbContext db, ICurrentUser user, StockLedger ledger, TimeProvider clock, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        CommonRules.RequiredId(errors, "IngredientId", r.IngredientId, "Ingredient");
        CommonRules.Positive(errors, "Quantity", r.Quantity, "Quantity", IngredientEndpoints.MaxQuantity);
        CommonRules.Required(errors, "Reason", r.Reason, "Reason", 500);
        CommonRules.OptionalUrl(errors, "EvidenceImageUrl", r.EvidenceImageUrl, "Evidence image URL");
        errors.ThrowIfAny();

        var branchId = BranchAccess.Resolve(user, r.BranchId);
        var ingredient = (await IngredientEndpoints.LoadForBranchAsync(db, branchId, [r.IngredientId!.Value], "IngredientId", ct))[r.IngredientId!.Value];

        if (r.Quantity > ingredient.QuantityOnHand)
        {
            throw new BusinessRuleException(
                "BR-31",
                $"Wasted quantity ({r.Quantity:0.###} {ingredient.Unit}) cannot exceed the current stock ({ingredient.QuantityOnHand:0.###} {ingredient.Unit}).");
        }

        var report = new WasteReport(branchId, StockLedger.NewDocumentNo("WR", clock.GetUtcNow().UtcDateTime), ingredient.Id, r.Quantity!.Value, r.Reason!, r.EvidenceImageUrl);
        db.WasteReports.Add(report);
        ledger.Apply(ingredient, StockTransactionType.Waste, -report.Quantity, report.Code, report.Id, report.Reason);
        await ledger.SaveAndPublishAsync(ct);

        return Results.CreatedAtRoute("GetWasteReport", new { id = report.Id }, (await MapAsync(db, [report], ct))[0]);
    }

    private static async Task<List<WasteReportResponse>> MapAsync(InventoryPurchasingDbContext db, IReadOnlyList<WasteReport> reports, CancellationToken ct)
    {
        var ids = reports.Select(w => w.IngredientId).Distinct().ToList();
        var ingredients = await db.Ingredients.AsNoTracking().Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        var reportIds = reports.Select(w => w.Id).ToList();
        var after = await db.StockTransactions.AsNoTracking()
            .Where(t => t.ReferenceId != null && reportIds.Contains(t.ReferenceId.Value))
            .ToDictionaryAsync(t => t.ReferenceId!.Value, t => t.QuantityAfter, ct);

        return reports.Select(w => new WasteReportResponse(
            w.Id, w.Code, w.BranchId, w.IngredientId, ingredients[w.IngredientId].Name, ingredients[w.IngredientId].Unit, w.Quantity,
            after.GetValueOrDefault(w.Id), w.Reason, w.EvidenceImageUrl, w.CreatedBy, w.CreatedAtUtc)).ToList();
    }
}
