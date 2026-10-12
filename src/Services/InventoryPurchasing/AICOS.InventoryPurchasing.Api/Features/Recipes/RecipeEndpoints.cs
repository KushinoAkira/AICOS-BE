using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.BuildingBlocks.Validation;
using AICOS.InventoryPurchasing.Api.Domain;
using AICOS.InventoryPurchasing.Api.Features.Ingredients;
using AICOS.InventoryPurchasing.Api.Infrastructure;
using AICOS.InventoryPurchasing.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.InventoryPurchasing.Api.Features.Recipes;

public sealed record RecipeLineRequest(Guid? IngredientId, decimal? Quantity);

public sealed record RecipeRequest(List<RecipeLineRequest>? Items);

public sealed record RecipeLineResponse(Guid IngredientId, string IngredientName, string Unit, decimal Quantity);

public sealed record RecipeResponse(Guid ProductId, Guid? BranchId, List<RecipeLineResponse> Items);

/// <summary>
/// Recipe = ingredients consumed by one unit of a menu product. Routes: /api/v1/inventory/recipes/{productId}.
/// Lives in Inventory (the stock owner); the product itself lives in Menu Service, which is called over REST
/// to check that the product exists and belongs to the same branch. PUT replaces the whole recipe.
/// </summary>
public static class RecipeEndpoints
{
    public static void MapRecipeEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/recipes").WithTags("Recipes");

        group.MapGet("/{productId:guid}", GetAsync).RequireAuthorization(AicosPolicies.Staff);
        group.MapPut("/{productId:guid}", ReplaceAsync).RequireAuthorization(AicosPolicies.Management);
    }

    private static async Task<RecipeResponse> GetAsync(Guid productId, InventoryPurchasingDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var items = await db.RecipeItems.AsNoTracking().Where(r => r.ProductId == productId).ToListAsync(ct);
        if (items.Count > 0)
        {
            BranchAccess.EnsureCanAccess(user, items[0].BranchId);
        }

        return await ToResponseAsync(db, productId, items.FirstOrDefault()?.BranchId, items, ct);
    }

    private static async Task<RecipeResponse> ReplaceAsync(
        Guid productId, RecipeRequest request, InventoryPurchasingDbContext db, ICurrentUser user, IMenuClient menu, CancellationToken ct)
    {
        var lines = request.Items ?? [];
        Validate(lines);

        var product = await menu.GetProductAsync(productId, ct)
            ?? throw new NotFoundException("Product", productId);
        BranchAccess.EnsureCanAccess(user, product.BranchId);

        await IngredientEndpoints.LoadForBranchAsync(db, product.BranchId, lines.Select(l => l.IngredientId!.Value), "Items", ct);

        db.RecipeItems.RemoveRange(await db.RecipeItems.Where(r => r.ProductId == productId).ToListAsync(ct));
        var items = lines.Select(l => new RecipeItem(product.BranchId, productId, l.IngredientId!.Value, l.Quantity!.Value)).ToList();
        db.RecipeItems.AddRange(items);
        await db.SaveChangesAsync(ct);

        return await ToResponseAsync(db, productId, product.BranchId, items, ct);
    }

    private static void Validate(List<RecipeLineRequest> lines)
    {
        var errors = new ValidationErrors();
        if (lines.Count > 50)
        {
            errors.Add("Items", "A recipe can have at most 50 ingredients.");
        }

        if (lines.Where(l => l.IngredientId is not null).GroupBy(l => l.IngredientId).Any(g => g.Count() > 1))
        {
            errors.Add("Items", "Each ingredient may appear only once in a recipe.");
        }

        for (var i = 0; i < lines.Count; i++)
        {
            CommonRules.RequiredId(errors, $"Items[{i}].IngredientId", lines[i].IngredientId, "Ingredient");
            CommonRules.Positive(errors, $"Items[{i}].Quantity", lines[i].Quantity, "Quantity", 100_000m);
        }

        errors.ThrowIfAny();
    }

    private static async Task<RecipeResponse> ToResponseAsync(
        InventoryPurchasingDbContext db, Guid productId, Guid? branchId, List<RecipeItem> items, CancellationToken ct)
    {
        var ids = items.Select(i => i.IngredientId).ToList();
        var ingredients = await db.Ingredients.AsNoTracking().Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        return new RecipeResponse(productId, branchId, items
            .Select(i => new RecipeLineResponse(i.IngredientId, ingredients[i.IngredientId].Name, ingredients[i.IngredientId].Unit, i.Quantity))
            .OrderBy(l => l.IngredientName)
            .ToList());
    }
}
