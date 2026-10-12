using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Persistence;
using AICOS.BuildingBlocks.Security;
using AICOS.BuildingBlocks.Validation;
using AICOS.InventoryPurchasing.Api.Domain;
using AICOS.InventoryPurchasing.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.InventoryPurchasing.Api.Features.Ingredients;

/// <summary>POST/PUT body. Stock is NOT set here - it only changes through stock movements (BR-11).</summary>
public sealed record IngredientRequest(Guid? BranchId, string? Name, string? Unit, decimal? ReorderLevel);

public sealed record ReorderLevelRequest(decimal? ReorderLevel);

public sealed record IngredientResponse(
    Guid Id, Guid BranchId, string Name, string Unit, decimal ReorderLevel, decimal QuantityOnHand, bool IsLowStock, DateTime? UpdatedAtUtc)
{
    public static IngredientResponse From(Ingredient i) =>
        new(i.Id, i.BranchId, i.Name, i.Unit, i.ReorderLevel, i.QuantityOnHand, i.IsLowStock, i.UpdatedAtUtc);
}

/// <summary>
/// UC-07 Manage Ingredients and Minimum Stock Threshold, UC-15 low-stock list. Routes: /api/v1/inventory/ingredients.
/// Read: warehouse roles. Write: SystemAdmin/Manager.
/// </summary>
public static class IngredientEndpoints
{
    public const decimal MaxQuantity = 10_000_000m;

    public static void MapIngredientEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/ingredients").WithTags("Ingredients");

        group.MapGet("/", ListAsync).RequireAuthorization(AicosPolicies.Warehouse);
        group.MapGet("/low-stock", LowStockAsync).RequireAuthorization(AicosPolicies.Warehouse);
        group.MapGet("/{id:guid}", GetAsync).RequireAuthorization(AicosPolicies.Warehouse).WithName("GetIngredient");
        group.MapPost("/", CreateAsync).RequireAuthorization(AicosPolicies.Management);
        group.MapPut("/{id:guid}", UpdateAsync).RequireAuthorization(AicosPolicies.Management);
        group.MapPatch("/{id:guid}/reorder-level", SetReorderLevelAsync).RequireAuthorization(AicosPolicies.Management);
        group.MapDelete("/{id:guid}", DeleteAsync).RequireAuthorization(AicosPolicies.Management);
    }

    private static Task<PagedResponse<IngredientResponse>> ListAsync(
        InventoryPurchasingDbContext db, ICurrentUser user, Guid? branchId, string? search, bool? lowStockOnly,
        int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        var branch = BranchAccess.ResolveFilter(user, branchId);
        var query = db.Ingredients.AsNoTracking();
        if (branch is not null)
        {
            query = query.Where(i => i.BranchId == branch);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(i => i.Name.ToLower().Contains(term));
        }

        if (lowStockOnly == true)
        {
            query = query.Where(i => i.QuantityOnHand < i.ReorderLevel);
        }

        return query.OrderBy(i => i.Name).ToPagedAsync(page, pageSize, IngredientResponse.From, ct);
    }

    /// <summary>UC-15: everything currently under its minimum stock, most critical first.</summary>
    private static async Task<List<IngredientResponse>> LowStockAsync(
        InventoryPurchasingDbContext db, ICurrentUser user, Guid? branchId, CancellationToken ct)
    {
        var branch = BranchAccess.ResolveFilter(user, branchId);
        var query = db.Ingredients.AsNoTracking().Where(i => i.QuantityOnHand < i.ReorderLevel);
        if (branch is not null)
        {
            query = query.Where(i => i.BranchId == branch);
        }

        var items = await query.ToListAsync(ct);
        return items
            .OrderBy(i => i.ReorderLevel == 0 ? 1 : i.QuantityOnHand / i.ReorderLevel)
            .Select(IngredientResponse.From)
            .ToList();
    }

    private static async Task<IngredientResponse> GetAsync(Guid id, InventoryPurchasingDbContext db, ICurrentUser user, CancellationToken ct) =>
        IngredientResponse.From(await FindAsync(db, user, id, ct));

    private static async Task<IResult> CreateAsync(IngredientRequest r, InventoryPurchasingDbContext db, ICurrentUser user, CancellationToken ct)
    {
        Validate(r);
        var branchId = BranchAccess.Resolve(user, r.BranchId);
        await EnsureNameUniqueAsync(db, branchId, r.Name!, null, ct);

        var ingredient = new Ingredient(branchId, r.Name!, r.Unit!, r.ReorderLevel!.Value);
        db.Ingredients.Add(ingredient);
        await db.SaveChangesAsync(ct);
        return Results.CreatedAtRoute("GetIngredient", new { id = ingredient.Id }, IngredientResponse.From(ingredient));
    }

    private static async Task<IngredientResponse> UpdateAsync(
        Guid id, IngredientRequest r, InventoryPurchasingDbContext db, ICurrentUser user, CancellationToken ct)
    {
        Validate(r);
        var ingredient = await FindAsync(db, user, id, ct);
        await EnsureNameUniqueAsync(db, ingredient.BranchId, r.Name!, id, ct);

        if (!string.Equals(ingredient.Unit, r.Unit!.Trim(), StringComparison.OrdinalIgnoreCase) && ingredient.QuantityOnHand != 0)
        {
            throw new BusinessRuleException("ingredient.unit_locked", "The unit cannot change while there is stock; it would change the meaning of the quantity.");
        }

        ingredient.Update(r.Name!, r.Unit!);
        ingredient.SetReorderLevel(r.ReorderLevel!.Value);
        await db.SaveChangesAsync(ct);
        return IngredientResponse.From(ingredient);
    }

    /// <summary>UC-07 "Minimum Stock Configuration" screen. BR-14: threshold cannot be negative.</summary>
    private static async Task<IngredientResponse> SetReorderLevelAsync(
        Guid id, ReorderLevelRequest r, InventoryPurchasingDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        CommonRules.NonNegative(errors, "ReorderLevel", r.ReorderLevel, "Reorder level", MaxQuantity);
        errors.ThrowIfAny();

        var ingredient = await FindAsync(db, user, id, ct);
        ingredient.SetReorderLevel(r.ReorderLevel!.Value);
        await db.SaveChangesAsync(ct);
        return IngredientResponse.From(ingredient);
    }

    private static async Task<IResult> DeleteAsync(Guid id, InventoryPurchasingDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var ingredient = await FindAsync(db, user, id, ct);
        var used = ingredient.QuantityOnHand != 0
                   || await db.StockTransactions.AnyAsync(t => t.IngredientId == id, ct)
                   || await db.RecipeItems.AnyAsync(r => r.IngredientId == id, ct)
                   || await db.PurchaseOrders.AnyAsync(p => p.Items.Any(i => i.IngredientId == id), ct);
        if (used)
        {
            throw new ConflictException("This ingredient has stock history, recipes or purchase orders and cannot be deleted.", "ingredient.in_use");
        }

        db.Ingredients.Remove(ingredient);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static void Validate(IngredientRequest r)
    {
        var errors = new ValidationErrors();
        CommonRules.Required(errors, "Name", r.Name, "Ingredient name", Ingredient.NameMaxLength);
        if (string.IsNullOrWhiteSpace(r.Unit) || !Ingredient.AllowedUnits.Contains(r.Unit.Trim().ToLowerInvariant()))
        {
            errors.Add("Unit", $"Unit must be one of: {string.Join(", ", Ingredient.AllowedUnits)}.");
        }

        CommonRules.NonNegative(errors, "ReorderLevel", r.ReorderLevel, "Reorder level", MaxQuantity);
        errors.ThrowIfAny();
    }

    private static async Task EnsureNameUniqueAsync(InventoryPurchasingDbContext db, Guid branchId, string name, Guid? excludeId, CancellationToken ct)
    {
        var normalized = name.Trim().ToLower();
        if (await db.Ingredients.AnyAsync(i => i.BranchId == branchId && i.Name.ToLower() == normalized && i.Id != excludeId, ct))
        {
            throw new ConflictException($"Ingredient '{name.Trim()}' already exists in this branch.", "ingredient.duplicate_name");
        }
    }

    internal static async Task<Ingredient> FindAsync(InventoryPurchasingDbContext db, ICurrentUser user, Guid id, CancellationToken ct)
    {
        var ingredient = await db.Ingredients.FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw new NotFoundException("Ingredient", id);
        BranchAccess.EnsureCanAccess(user, ingredient.BranchId);
        return ingredient;
    }

    /// <summary>Loads ingredients of one branch by id; 400 listing any id that does not exist there.</summary>
    internal static async Task<Dictionary<Guid, Ingredient>> LoadForBranchAsync(
        InventoryPurchasingDbContext db, Guid branchId, IEnumerable<Guid> ids, string field, CancellationToken ct)
    {
        var wanted = ids.Distinct().ToList();
        var found = await db.Ingredients.Where(i => i.BranchId == branchId && wanted.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        var missing = wanted.Where(id => !found.ContainsKey(id)).ToList();
        if (missing.Count > 0)
        {
            throw new ValidationFailedException(field, $"Ingredients not found in this branch: {string.Join(", ", missing)}.");
        }

        return found;
    }
}
