using AICOS.BuildingBlocks.Persistence;
using AICOS.BuildingBlocks.Security;
using AICOS.BuildingBlocks.Validation;
using AICOS.InventoryPurchasing.Api.Domain;
using AICOS.InventoryPurchasing.Api.Features.Ingredients;
using AICOS.InventoryPurchasing.Api.Infrastructure;
using AICOS.InventoryPurchasing.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.InventoryPurchasing.Api.Features.StockMovements;

public enum ManualMovementType
{
    Import,
    Export,
}

public sealed record StockMovementLineRequest(Guid? IngredientId, decimal? Quantity);

/// <summary>One stock document (phiếu nhập / phiếu xuất) with one or more lines. BR-11: <see cref="DocumentNo"/> is required.</summary>
public sealed record StockMovementRequest(Guid? BranchId, ManualMovementType? Type, string? DocumentNo, string? Note, List<StockMovementLineRequest>? Lines);

public sealed record StockTransactionResponse(
    Guid Id, Guid BranchId, Guid IngredientId, string IngredientName, string Unit, StockTransactionType Type,
    decimal QuantityDelta, decimal QuantityAfter, string DocumentNo, Guid? ReferenceId, string? Note, Guid? CreatedBy, DateTime CreatedAtUtc);

/// <summary>
/// UC-20 Record Stock Import/Export. Routes: /api/v1/inventory/stock-movements. Warehouse roles.
/// The whole document is applied atomically: if one line would make stock negative, nothing is saved (422).
/// </summary>
public static class StockMovementEndpoints
{
    public static void MapStockMovementEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/stock-movements").WithTags("Stock movements").RequireAuthorization(AicosPolicies.Warehouse);

        group.MapPost("/", CreateAsync);
        group.MapGet("/", ListAsync);
    }

    private static async Task<IResult> CreateAsync(
        StockMovementRequest r, InventoryPurchasingDbContext db, ICurrentUser user, StockLedger ledger, CancellationToken ct)
    {
        Validate(r);
        var branchId = BranchAccess.Resolve(user, r.BranchId);
        var lines = r.Lines!;
        var ingredients = await IngredientEndpoints.LoadForBranchAsync(db, branchId, lines.Select(l => l.IngredientId!.Value), "Lines", ct);

        var type = r.Type == ManualMovementType.Import ? StockTransactionType.Import : StockTransactionType.Export;
        var created = lines.Select(line => ledger.Apply(
                ingredients[line.IngredientId!.Value],
                type,
                type == StockTransactionType.Import ? line.Quantity!.Value : -line.Quantity!.Value,
                r.DocumentNo!.Trim(),
                referenceId: null,
                r.Note?.Trim()))
            .ToList();

        await ledger.SaveAndPublishAsync(ct);
        return Results.Created((string?)null, await MapAsync(db, created, ct));
    }

    private static async Task<PagedResponse<StockTransactionResponse>> ListAsync(
        InventoryPurchasingDbContext db, ICurrentUser user, Guid? branchId, Guid? ingredientId, StockTransactionType? type,
        string? documentNo, DateTimeOffset? from, DateTimeOffset? to, int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        var branch = BranchAccess.ResolveFilter(user, branchId);
        var query = db.StockTransactions.AsNoTracking();
        if (branch is not null)
        {
            query = query.Where(t => t.BranchId == branch);
        }

        if (ingredientId is not null)
        {
            query = query.Where(t => t.IngredientId == ingredientId);
        }

        if (type is not null)
        {
            query = query.Where(t => t.Type == type);
        }

        if (!string.IsNullOrWhiteSpace(documentNo))
        {
            var doc = documentNo.Trim();
            query = query.Where(t => t.DocumentNo == doc);
        }

        if (from is not null)
        {
            var fromUtc = from.Value.UtcDateTime;
            query = query.Where(t => t.CreatedAtUtc >= fromUtc);
        }

        if (to is not null)
        {
            var toUtc = to.Value.UtcDateTime;
            query = query.Where(t => t.CreatedAtUtc < toUtc);
        }

        var paged = await query.OrderByDescending(t => t.CreatedAtUtc).ToPagedAsync(page, pageSize, t => t, ct);
        var items = await MapAsync(db, paged.Items, ct);
        return new PagedResponse<StockTransactionResponse>(items, paged.Page, paged.PageSize, paged.TotalCount);
    }

    private static void Validate(StockMovementRequest r)
    {
        var errors = new ValidationErrors();
        CommonRules.RequiredEnum(errors, "Type", r.Type, "Type");
        CommonRules.Required(errors, "DocumentNo", r.DocumentNo, "Document number (BR-11)", 50);
        CommonRules.MaxLength(errors, "Note", r.Note, "Note", 500);
        ValidateLines(errors, r.Lines?.Select(l => (l.IngredientId, l.Quantity)).ToList(), "Lines");
        errors.ThrowIfAny();
    }

    internal static void ValidateLines(ValidationErrors errors, List<(Guid? IngredientId, decimal? Quantity)>? lines, string field)
    {
        if (lines is null || lines.Count == 0)
        {
            errors.Add(field, "At least one line is required.");
            return;
        }

        if (lines.Count > 100)
        {
            errors.Add(field, "At most 100 lines per document.");
        }

        if (lines.Where(l => l.IngredientId is not null).GroupBy(l => l.IngredientId).Any(g => g.Count() > 1))
        {
            errors.Add(field, "Each ingredient may appear only once per document.");
        }

        for (var i = 0; i < lines.Count; i++)
        {
            CommonRules.RequiredId(errors, $"{field}[{i}].IngredientId", lines[i].IngredientId, "Ingredient");
            CommonRules.Positive(errors, $"{field}[{i}].Quantity", lines[i].Quantity, "Quantity", IngredientEndpoints.MaxQuantity);
        }
    }

    /// <summary>Adds ingredient name/unit to ledger lines (one extra query, no tuple projections in SQL).</summary>
    internal static async Task<List<StockTransactionResponse>> MapAsync(
        InventoryPurchasingDbContext db, IReadOnlyList<StockTransaction> transactions, CancellationToken ct)
    {
        var ids = transactions.Select(t => t.IngredientId).Distinct().ToList();
        var ingredients = await db.Ingredients.AsNoTracking().Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        return transactions.Select(t => new StockTransactionResponse(
            t.Id, t.BranchId, t.IngredientId, ingredients[t.IngredientId].Name, ingredients[t.IngredientId].Unit, t.Type,
            t.QuantityDelta, t.QuantityAfter, t.DocumentNo, t.ReferenceId, t.Note, t.CreatedBy, t.CreatedAtUtc)).ToList();
    }
}
