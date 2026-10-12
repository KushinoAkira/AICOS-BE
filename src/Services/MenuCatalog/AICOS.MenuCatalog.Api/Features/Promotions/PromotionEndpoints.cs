using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Persistence;
using AICOS.BuildingBlocks.Security;
using AICOS.BuildingBlocks.Validation;
using AICOS.MenuCatalog.Api.Domain;
using AICOS.MenuCatalog.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.MenuCatalog.Api.Features.Promotions;

/// <summary>Dates are ISO-8601 (e.g. "2026-12-01T00:00:00+07:00"); they are stored in UTC.</summary>
public sealed record PromotionRequest(
    Guid? BranchId,
    string? Name,
    string? Description,
    DiscountType? DiscountType,
    decimal? DiscountValue,
    DateTimeOffset? StartDate,
    DateTimeOffset? EndDate,
    List<Guid>? ProductIds);

public sealed record ChangePromotionStatusRequest(PromotionStatus? Status);

public sealed record PromotionResponse(
    Guid Id,
    Guid BranchId,
    string Name,
    string? Description,
    DiscountType DiscountType,
    decimal DiscountValue,
    DateTime StartDateUtc,
    DateTime EndDateUtc,
    PromotionStatus Status,
    bool IsRunning,
    List<Guid> ProductIds)
{
    public static PromotionResponse From(Promotion p, DateTime nowUtc) => new(
        p.Id, p.BranchId, p.Name, p.Description, p.DiscountType, p.DiscountValue, p.StartDateUtc, p.EndDateUtc,
        p.Status, p.IsRunning(nowUtc), p.Products.Select(x => x.ProductId).ToList());
}

/// <summary>UC-14 Configure Promotional Campaign. Routes: /api/v1/menu/promotions. Read: staff. Write: SystemAdmin/Manager.</summary>
public static class PromotionEndpoints
{
    public static void MapPromotionEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/promotions").WithTags("Promotions");

        group.MapGet("/", ListAsync).RequireAuthorization(AicosPolicies.Staff);
        group.MapGet("/{id:guid}", GetAsync).RequireAuthorization(AicosPolicies.Staff).WithName("GetPromotion");
        group.MapPost("/", CreateAsync).RequireAuthorization(AicosPolicies.Management);
        group.MapPut("/{id:guid}", UpdateAsync).RequireAuthorization(AicosPolicies.Management);
        group.MapPatch("/{id:guid}/status", ChangeStatusAsync).RequireAuthorization(AicosPolicies.Management);
        group.MapDelete("/{id:guid}", DeleteAsync).RequireAuthorization(AicosPolicies.Management);
    }

    private static async Task<PagedResponse<PromotionResponse>> ListAsync(
        MenuCatalogDbContext db,
        ICurrentUser user,
        TimeProvider clock,
        Guid? branchId,
        PromotionStatus? status,
        bool? runningOnly,
        int page = 1,
        int pageSize = 20,
        CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var branch = BranchAccess.ResolveFilter(user, branchId);
        var query = db.Promotions.AsNoTracking().Include(p => p.Products).AsQueryable();

        if (branch is not null)
        {
            query = query.Where(p => p.BranchId == branch);
        }

        if (status is not null)
        {
            query = query.Where(p => p.Status == status);
        }

        if (runningOnly == true)
        {
            query = query.Where(p => p.Status == PromotionStatus.Active && p.StartDateUtc <= now && now < p.EndDateUtc);
        }

        return await query.OrderByDescending(p => p.StartDateUtc).ToPagedAsync(page, pageSize, p => PromotionResponse.From(p, now), ct);
    }

    private static async Task<PromotionResponse> GetAsync(Guid id, MenuCatalogDbContext db, ICurrentUser user, TimeProvider clock, CancellationToken ct) =>
        PromotionResponse.From(await FindAsync(db, user, id, ct), clock.GetUtcNow().UtcDateTime);

    private static async Task<IResult> CreateAsync(
        PromotionRequest request, MenuCatalogDbContext db, ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        Validate(request, now, isCreate: true);
        var branchId = BranchAccess.Resolve(user, request.BranchId);
        await EnsureProductsInBranchAsync(db, branchId, request.ProductIds!, ct);

        var promotion = new Promotion(
            branchId, request.Name!, request.Description, request.DiscountType!.Value, request.DiscountValue!.Value,
            request.StartDate!.Value.UtcDateTime, request.EndDate!.Value.UtcDateTime, request.ProductIds!);
        db.Promotions.Add(promotion);
        await db.SaveChangesAsync(ct);

        return Results.CreatedAtRoute("GetPromotion", new { id = promotion.Id }, PromotionResponse.From(promotion, now));
    }

    private static async Task<PromotionResponse> UpdateAsync(
        Guid id, PromotionRequest request, MenuCatalogDbContext db, ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        Validate(request, now, isCreate: false);
        var promotion = await FindAsync(db, user, id, ct);
        await EnsureProductsInBranchAsync(db, promotion.BranchId, request.ProductIds!, ct);

        promotion.Update(
            request.Name!, request.Description, request.DiscountType!.Value, request.DiscountValue!.Value,
            request.StartDate!.Value.UtcDateTime, request.EndDate!.Value.UtcDateTime, request.ProductIds!);
        await db.SaveChangesAsync(ct);
        return PromotionResponse.From(promotion, now);
    }

    private static async Task<PromotionResponse> ChangeStatusAsync(
        Guid id, ChangePromotionStatusRequest request, MenuCatalogDbContext db, ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        CommonRules.RequiredEnum(errors, "Status", request.Status, "Status");
        errors.ThrowIfAny();

        var promotion = await FindAsync(db, user, id, ct);
        promotion.ChangeStatus(request.Status!.Value);
        await db.SaveChangesAsync(ct);
        return PromotionResponse.From(promotion, clock.GetUtcNow().UtcDateTime);
    }

    private static async Task<IResult> DeleteAsync(Guid id, MenuCatalogDbContext db, ICurrentUser user, CancellationToken ct)
    {
        db.Promotions.Remove(await FindAsync(db, user, id, ct));
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static void Validate(PromotionRequest r, DateTime nowUtc, bool isCreate)
    {
        var errors = new ValidationErrors();
        CommonRules.Required(errors, "Name", r.Name, "Promotion name", Promotion.NameMaxLength);
        CommonRules.MaxLength(errors, "Description", r.Description, "Description", 2000);
        CommonRules.RequiredEnum(errors, "DiscountType", r.DiscountType, "Discount type");

        if (r.DiscountType == DiscountType.Percentage && r.DiscountValue is not (>= 1 and <= 100))
        {
            errors.Add("DiscountValue", "Percentage discount must be between 1 and 100.");
        }
        else if (r.DiscountType == DiscountType.FixedAmount)
        {
            CommonRules.Positive(errors, "DiscountValue", r.DiscountValue, "Discount amount", 100_000_000m);
        }

        if (r.StartDate is null)
        {
            errors.Add("StartDate", "Start date is required.");
        }

        if (r.EndDate is null)
        {
            errors.Add("EndDate", "End date is required.");
        }
        else if (r.StartDate is not null && r.StartDate >= r.EndDate)
        {
            errors.Add("EndDate", "BR-26: the start date must be before the end date.");
        }
        else if (isCreate && r.EndDate.Value.UtcDateTime <= nowUtc)
        {
            errors.Add("EndDate", "End date must be in the future.");
        }

        if (r.ProductIds is null || r.ProductIds.Count == 0)
        {
            errors.Add("ProductIds", "Select at least one product.");
        }
        else if (r.ProductIds.Count > 200)
        {
            errors.Add("ProductIds", "A promotion can contain at most 200 products.");
        }

        errors.ThrowIfAny();
    }

    private static async Task EnsureProductsInBranchAsync(MenuCatalogDbContext db, Guid branchId, List<Guid> productIds, CancellationToken ct)
    {
        var ids = productIds.Distinct().ToList();
        var found = await db.Products.Where(p => ids.Contains(p.Id) && p.BranchId == branchId).Select(p => p.Id).ToListAsync(ct);
        var missing = ids.Except(found).ToList();
        if (missing.Count > 0)
        {
            throw new ValidationFailedException("ProductIds", $"Products not found in this branch: {string.Join(", ", missing)}.");
        }
    }

    private static async Task<Promotion> FindAsync(MenuCatalogDbContext db, ICurrentUser user, Guid id, CancellationToken ct)
    {
        var promotion = await db.Promotions.Include(p => p.Products).FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new NotFoundException("Promotion", id);
        BranchAccess.EnsureCanAccess(user, promotion.BranchId);
        return promotion;
    }
}
