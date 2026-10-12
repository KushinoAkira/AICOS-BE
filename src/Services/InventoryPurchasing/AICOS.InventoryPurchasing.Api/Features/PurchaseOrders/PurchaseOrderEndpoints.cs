using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Persistence;
using AICOS.BuildingBlocks.Security;
using AICOS.BuildingBlocks.Validation;
using AICOS.InventoryPurchasing.Api.Domain;
using AICOS.InventoryPurchasing.Api.Features.Ingredients;
using AICOS.InventoryPurchasing.Api.Features.Suppliers;
using AICOS.InventoryPurchasing.Api.Infrastructure;
using AICOS.InventoryPurchasing.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.InventoryPurchasing.Api.Features.PurchaseOrders;

public sealed record PurchaseOrderItemRequest(Guid? IngredientId, decimal? Quantity, decimal? UnitPrice);

public sealed record PurchaseOrderRequest(Guid? BranchId, Guid? SupplierId, DateOnly? ExpectedDate, string? Note, List<PurchaseOrderItemRequest>? Items);

public sealed record RejectRequest(string? Reason);

public sealed record PurchaseOrderItemResponse(Guid IngredientId, string IngredientName, string Unit, decimal Quantity, decimal UnitPrice, decimal LineTotal);

public sealed record PurchaseOrderResponse(
    Guid Id, string Code, Guid BranchId, Guid SupplierId, string SupplierName, PurchaseOrderStatus Status, DateOnly? ExpectedDate,
    string? Note, string? RejectReason, decimal TotalAmount, Guid? CreatedBy, DateTime CreatedAtUtc, Guid? DecidedBy,
    DateTime? DecidedAtUtc, DateTime? ReceivedAtUtc, List<PurchaseOrderItemResponse> Items);

/// <summary>
/// UC-22 Place Purchase Order, UC-09 Approve Purchase Order. Routes: /api/v1/inventory/purchase-orders.
/// Flow: create (PendingApproval) -> Manager approve/reject (BR-12) -> warehouse receive (stock import) ; cancel before receipt.
/// </summary>
public static class PurchaseOrderEndpoints
{
    public static void MapPurchaseOrderEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/purchase-orders").WithTags("Purchase orders");

        group.MapGet("/", ListAsync).RequireAuthorization(AicosPolicies.Warehouse);
        group.MapGet("/{id:guid}", GetAsync).RequireAuthorization(AicosPolicies.Warehouse).WithName("GetPurchaseOrder");
        group.MapPost("/", CreateAsync).RequireAuthorization(AicosPolicies.Warehouse);
        group.MapPut("/{id:guid}", UpdateAsync).RequireAuthorization(AicosPolicies.Warehouse);
        group.MapPost("/{id:guid}/approve", ApproveAsync).RequireAuthorization(AicosPolicies.Management);
        group.MapPost("/{id:guid}/reject", RejectAsync).RequireAuthorization(AicosPolicies.Management);
        group.MapPost("/{id:guid}/receive", ReceiveAsync).RequireAuthorization(AicosPolicies.Warehouse);
        group.MapPost("/{id:guid}/cancel", CancelAsync).RequireAuthorization(AicosPolicies.Warehouse);
    }

    private static async Task<PagedResponse<PurchaseOrderResponse>> ListAsync(
        InventoryPurchasingDbContext db, ICurrentUser user, Guid? branchId, PurchaseOrderStatus? status, Guid? supplierId,
        int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        var branch = BranchAccess.ResolveFilter(user, branchId);
        var query = db.PurchaseOrders.AsNoTracking().Include(p => p.Items).AsQueryable();
        if (branch is not null)
        {
            query = query.Where(p => p.BranchId == branch);
        }

        if (status is not null)
        {
            query = query.Where(p => p.Status == status);
        }

        if (supplierId is not null)
        {
            query = query.Where(p => p.SupplierId == supplierId);
        }

        var paged = await query.OrderByDescending(p => p.CreatedAtUtc).ToPagedAsync(page, pageSize, p => p, ct);
        var items = new List<PurchaseOrderResponse>();
        foreach (var order in paged.Items)
        {
            items.Add(await MapAsync(db, order, ct));
        }

        return new PagedResponse<PurchaseOrderResponse>(items, paged.Page, paged.PageSize, paged.TotalCount);
    }

    private static async Task<PurchaseOrderResponse> GetAsync(Guid id, InventoryPurchasingDbContext db, ICurrentUser user, CancellationToken ct) =>
        await MapAsync(db, await FindAsync(db, user, id, ct), ct);

    private static async Task<IResult> CreateAsync(
        PurchaseOrderRequest r, InventoryPurchasingDbContext db, ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        Validate(r, clock);
        var branchId = BranchAccess.Resolve(user, r.BranchId);
        await EnsureSupplierActiveAsync(db, user, r.SupplierId!.Value, branchId, ct);
        await IngredientEndpoints.LoadForBranchAsync(db, branchId, r.Items!.Select(i => i.IngredientId!.Value), "Items", ct);

        var order = new PurchaseOrder(
            branchId, r.SupplierId!.Value, StockLedger.NewDocumentNo("PO", clock.GetUtcNow().UtcDateTime), r.ExpectedDate, r.Note, ToItems(r));
        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync(ct);

        return Results.CreatedAtRoute("GetPurchaseOrder", new { id = order.Id }, await MapAsync(db, order, ct));
    }

    private static async Task<PurchaseOrderResponse> UpdateAsync(
        Guid id, PurchaseOrderRequest r, InventoryPurchasingDbContext db, ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        Validate(r, clock);
        var order = await FindAsync(db, user, id, ct);
        await EnsureSupplierActiveAsync(db, user, r.SupplierId!.Value, order.BranchId, ct);
        await IngredientEndpoints.LoadForBranchAsync(db, order.BranchId, r.Items!.Select(i => i.IngredientId!.Value), "Items", ct);

        order.Update(r.SupplierId!.Value, r.ExpectedDate, r.Note, ToItems(r));
        await db.SaveChangesAsync(ct);
        return await MapAsync(db, order, ct);
    }

    private static async Task<PurchaseOrderResponse> ApproveAsync(
        Guid id, InventoryPurchasingDbContext db, ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        var order = await FindAsync(db, user, id, ct);
        order.Approve(user.UserId, clock.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct);
        return await MapAsync(db, order, ct);
    }

    private static async Task<PurchaseOrderResponse> RejectAsync(
        Guid id, RejectRequest r, InventoryPurchasingDbContext db, ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        CommonRules.Required(errors, "Reason", r.Reason, "Reject reason", 500);
        errors.ThrowIfAny();

        var order = await FindAsync(db, user, id, ct);
        order.Reject(user.UserId, r.Reason!, clock.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct);
        return await MapAsync(db, order, ct);
    }

    /// <summary>Goods arrived: imports every line into stock with the PO code as document number (BR-11).</summary>
    private static async Task<PurchaseOrderResponse> ReceiveAsync(
        Guid id, InventoryPurchasingDbContext db, ICurrentUser user, StockLedger ledger, TimeProvider clock, CancellationToken ct)
    {
        var order = await FindAsync(db, user, id, ct);
        order.MarkReceived(clock.GetUtcNow().UtcDateTime);

        var ingredients = await IngredientEndpoints.LoadForBranchAsync(db, order.BranchId, order.Items.Select(i => i.IngredientId), "Items", ct);
        foreach (var item in order.Items)
        {
            ledger.Apply(ingredients[item.IngredientId], StockTransactionType.Import, item.Quantity, order.Code, order.Id, "Purchase order receipt");
        }

        await ledger.SaveAndPublishAsync(ct);
        return await MapAsync(db, order, ct);
    }

    private static async Task<PurchaseOrderResponse> CancelAsync(Guid id, InventoryPurchasingDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var order = await FindAsync(db, user, id, ct);
        order.Cancel();
        await db.SaveChangesAsync(ct);
        return await MapAsync(db, order, ct);
    }

    private static void Validate(PurchaseOrderRequest r, TimeProvider clock)
    {
        var errors = new ValidationErrors();
        CommonRules.RequiredId(errors, "SupplierId", r.SupplierId, "Supplier");
        CommonRules.MaxLength(errors, "Note", r.Note, "Note", 1000);

        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        if (r.ExpectedDate is not null && r.ExpectedDate < today)
        {
            errors.Add("ExpectedDate", "Expected delivery date cannot be in the past.");
        }

        var items = r.Items ?? [];
        if (items.Count is 0 or > 100)
        {
            errors.Add("Items", "A purchase order needs 1-100 items.");
        }

        if (items.Where(i => i.IngredientId is not null).GroupBy(i => i.IngredientId).Any(g => g.Count() > 1))
        {
            errors.Add("Items", "Each ingredient may appear only once per purchase order.");
        }

        for (var i = 0; i < items.Count; i++)
        {
            CommonRules.RequiredId(errors, $"Items[{i}].IngredientId", items[i].IngredientId, "Ingredient");
            CommonRules.Positive(errors, $"Items[{i}].Quantity", items[i].Quantity, "Quantity", IngredientEndpoints.MaxQuantity);
            CommonRules.NonNegative(errors, $"Items[{i}].UnitPrice", items[i].UnitPrice, "Unit price", 100_000_000m);
        }

        errors.ThrowIfAny();
    }

    private static List<PurchaseOrderItem> ToItems(PurchaseOrderRequest r) =>
        r.Items!.Select(i => new PurchaseOrderItem(i.IngredientId!.Value, i.Quantity!.Value, i.UnitPrice!.Value)).ToList();

    private static async Task EnsureSupplierActiveAsync(
        InventoryPurchasingDbContext db, ICurrentUser user, Guid supplierId, Guid branchId, CancellationToken ct)
    {
        var supplier = await db.Suppliers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == supplierId, ct);
        if (supplier is null || supplier.BranchId != branchId)
        {
            throw new ValidationFailedException("SupplierId", "Supplier does not exist in this branch.");
        }

        if (supplier.Status != SupplierStatus.Active)
        {
            throw new ValidationFailedException("SupplierId", $"Supplier '{supplier.Name}' is inactive.");
        }
    }

    private static async Task<PurchaseOrder> FindAsync(InventoryPurchasingDbContext db, ICurrentUser user, Guid id, CancellationToken ct)
    {
        var order = await db.PurchaseOrders.Include(p => p.Items).FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new NotFoundException("Purchase order", id);
        BranchAccess.EnsureCanAccess(user, order.BranchId);
        return order;
    }

    private static async Task<PurchaseOrderResponse> MapAsync(InventoryPurchasingDbContext db, PurchaseOrder o, CancellationToken ct)
    {
        var supplierName = await db.Suppliers.Where(s => s.Id == o.SupplierId).Select(s => s.Name).FirstAsync(ct);
        var ids = o.Items.Select(i => i.IngredientId).ToList();
        var ingredients = await db.Ingredients.AsNoTracking().Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);

        return new PurchaseOrderResponse(
            o.Id, o.Code, o.BranchId, o.SupplierId, supplierName, o.Status, o.ExpectedDate, o.Note, o.RejectReason, o.TotalAmount,
            o.CreatedBy, o.CreatedAtUtc, o.DecidedBy, o.DecidedAtUtc, o.ReceivedAtUtc,
            o.Items.Select(i => new PurchaseOrderItemResponse(
                i.IngredientId, ingredients[i.IngredientId].Name, ingredients[i.IngredientId].Unit, i.Quantity, i.UnitPrice, i.Quantity * i.UnitPrice))
                .ToList());
    }
}
