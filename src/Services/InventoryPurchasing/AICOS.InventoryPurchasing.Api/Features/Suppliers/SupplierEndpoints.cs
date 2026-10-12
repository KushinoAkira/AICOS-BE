using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Persistence;
using AICOS.BuildingBlocks.Security;
using AICOS.BuildingBlocks.Validation;
using AICOS.InventoryPurchasing.Api.Domain;
using AICOS.InventoryPurchasing.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.InventoryPurchasing.Api.Features.Suppliers;

public sealed record SupplierRequest(Guid? BranchId, string? Name, string? ContactName, string? Phone, string? Email, string? Address);

public sealed record ChangeSupplierStatusRequest(SupplierStatus? Status);

public sealed record SupplierResponse(
    Guid Id, Guid BranchId, string Name, string? ContactName, string? Phone, string? Email, string? Address, SupplierStatus Status)
{
    public static SupplierResponse From(Supplier s) => new(s.Id, s.BranchId, s.Name, s.ContactName, s.Phone, s.Email, s.Address, s.Status);
}

/// <summary>
/// UC-08 Manage Suppliers. Routes: /api/v1/inventory/suppliers. Read: warehouse roles. Write: SystemAdmin/Manager.
/// Suppliers are deactivated, never deleted (purchase orders reference them).
/// </summary>
public static class SupplierEndpoints
{
    public static void MapSupplierEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/suppliers").WithTags("Suppliers");

        group.MapGet("/", ListAsync).RequireAuthorization(AicosPolicies.Warehouse);
        group.MapGet("/{id:guid}", GetAsync).RequireAuthorization(AicosPolicies.Warehouse).WithName("GetSupplier");
        group.MapPost("/", CreateAsync).RequireAuthorization(AicosPolicies.Management);
        group.MapPut("/{id:guid}", UpdateAsync).RequireAuthorization(AicosPolicies.Management);
        group.MapPatch("/{id:guid}/status", ChangeStatusAsync).RequireAuthorization(AicosPolicies.Management);
    }

    private static Task<PagedResponse<SupplierResponse>> ListAsync(
        InventoryPurchasingDbContext db, ICurrentUser user, Guid? branchId, string? search, SupplierStatus? status,
        int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        var branch = BranchAccess.ResolveFilter(user, branchId);
        var query = db.Suppliers.AsNoTracking();
        if (branch is not null)
        {
            query = query.Where(s => s.BranchId == branch);
        }

        if (status is not null)
        {
            query = query.Where(s => s.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(s => s.Name.ToLower().Contains(term)
                                     || (s.ContactName != null && s.ContactName.ToLower().Contains(term))
                                     || (s.Phone != null && s.Phone.Contains(term)));
        }

        return query.OrderBy(s => s.Name).ToPagedAsync(page, pageSize, SupplierResponse.From, ct);
    }

    private static async Task<SupplierResponse> GetAsync(Guid id, InventoryPurchasingDbContext db, ICurrentUser user, CancellationToken ct) =>
        SupplierResponse.From(await FindAsync(db, user, id, ct));

    private static async Task<IResult> CreateAsync(SupplierRequest r, InventoryPurchasingDbContext db, ICurrentUser user, CancellationToken ct)
    {
        Validate(r);
        var branchId = BranchAccess.Resolve(user, r.BranchId);
        await EnsureNameUniqueAsync(db, branchId, r.Name!, null, ct);

        var supplier = new Supplier(branchId, r.Name!, r.ContactName, r.Phone, r.Email, r.Address);
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync(ct);
        return Results.CreatedAtRoute("GetSupplier", new { id = supplier.Id }, SupplierResponse.From(supplier));
    }

    private static async Task<SupplierResponse> UpdateAsync(
        Guid id, SupplierRequest r, InventoryPurchasingDbContext db, ICurrentUser user, CancellationToken ct)
    {
        Validate(r);
        var supplier = await FindAsync(db, user, id, ct);
        await EnsureNameUniqueAsync(db, supplier.BranchId, r.Name!, id, ct);
        supplier.Update(r.Name!, r.ContactName, r.Phone, r.Email, r.Address);
        await db.SaveChangesAsync(ct);
        return SupplierResponse.From(supplier);
    }

    private static async Task<SupplierResponse> ChangeStatusAsync(
        Guid id, ChangeSupplierStatusRequest r, InventoryPurchasingDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        CommonRules.RequiredEnum(errors, "Status", r.Status, "Status");
        errors.ThrowIfAny();

        var supplier = await FindAsync(db, user, id, ct);
        supplier.ChangeStatus(r.Status!.Value);
        await db.SaveChangesAsync(ct);
        return SupplierResponse.From(supplier);
    }

    private static void Validate(SupplierRequest r)
    {
        var errors = new ValidationErrors();
        CommonRules.Required(errors, "Name", r.Name, "Supplier name", Supplier.NameMaxLength);
        CommonRules.MaxLength(errors, "ContactName", r.ContactName, "Contact name", 255);
        CommonRules.Phone(errors, "Phone", r.Phone);
        CommonRules.OptionalEmail(errors, "Email", r.Email);
        CommonRules.MaxLength(errors, "Address", r.Address, "Address", 500);
        if (string.IsNullOrWhiteSpace(r.Phone) && string.IsNullOrWhiteSpace(r.Email))
        {
            errors.Add("Phone", "Provide at least a phone number or an email so the supplier can be contacted.");
        }

        errors.ThrowIfAny();
    }

    private static async Task EnsureNameUniqueAsync(InventoryPurchasingDbContext db, Guid branchId, string name, Guid? excludeId, CancellationToken ct)
    {
        var normalized = name.Trim().ToLower();
        if (await db.Suppliers.AnyAsync(s => s.BranchId == branchId && s.Name.ToLower() == normalized && s.Id != excludeId, ct))
        {
            throw new ConflictException($"Supplier '{name.Trim()}' already exists in this branch.", "supplier.duplicate_name");
        }
    }

    internal static async Task<Supplier> FindAsync(InventoryPurchasingDbContext db, ICurrentUser user, Guid id, CancellationToken ct)
    {
        var supplier = await db.Suppliers.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw new NotFoundException("Supplier", id);
        BranchAccess.EnsureCanAccess(user, supplier.BranchId);
        return supplier;
    }
}
