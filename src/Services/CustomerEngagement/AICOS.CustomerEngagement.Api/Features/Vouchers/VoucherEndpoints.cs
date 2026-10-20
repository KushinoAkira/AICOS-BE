using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Persistence;
using AICOS.BuildingBlocks.Security;
using AICOS.BuildingBlocks.Validation;
using AICOS.CustomerEngagement.Api.Domain;
using AICOS.CustomerEngagement.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.CustomerEngagement.Api.Features.Vouchers;

/// <summary><see cref="Code"/> is optional (generated when empty). Gift cards are always a fixed amount.</summary>
public sealed record CreateVoucherRequest(
    Guid? BranchId, string? Code, VoucherType? Type, VoucherDiscountType? DiscountType, decimal? Value, decimal? MinOrderAmount,
    DateTimeOffset? ExpiresAt, string? IssuedToName, Guid? IssuedToCustomerId);

public sealed record RevokeVoucherRequest(string? Reason);

public sealed record ValidateVoucherRequest(string? Code, decimal? OrderAmount);

public sealed record RedeemVoucherRequest(string? Code, decimal? OrderAmount, string? OrderReference);

public sealed record VoucherResponse(
    Guid Id, Guid BranchId, string Code, VoucherType Type, VoucherDiscountType DiscountType, decimal Value, decimal? MinOrderAmount,
    DateTime ExpiresAtUtc, bool IsExpired, VoucherStatus Status, string? IssuedToName, Guid? IssuedToCustomerId,
    DateTime? RedeemedAtUtc, string? RedeemedOrderReference, decimal? RedeemedDiscount, string? RevokeReason, DateTime CreatedAtUtc)
{
    public static VoucherResponse From(Voucher v, DateTime nowUtc) => new(
        v.Id, v.BranchId, v.Code, v.Type, v.DiscountType, v.Value, v.MinOrderAmount, v.ExpiresAtUtc, v.IsExpired(nowUtc), v.Status,
        v.IssuedToName, v.IssuedToCustomerId, v.RedeemedAtUtc, v.RedeemedOrderReference, v.RedeemedDiscount, v.RevokeReason, v.CreatedAtUtc);
}

public sealed record VoucherQuoteResponse(string Code, decimal OrderAmount, decimal Discount, decimal AmountToPay);

/// <summary>
/// UC-41 Manage Gift Card / Voucher. Routes: /api/v1/customers/vouchers.
/// Issue / list / revoke: BrandAdmin/Manager. Validate (preview) and redeem at the counter: all staff.
/// BR-35: expired, redeemed or revoked codes are rejected (422).
/// </summary>
public static partial class VoucherEndpoints
{
    public static void MapVoucherEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/vouchers").WithTags("Vouchers");

        group.MapGet("/", ListAsync).RequireAuthorization(AicosPolicies.Management);
        group.MapGet("/{id:guid}", GetAsync).RequireAuthorization(AicosPolicies.Management).WithName("GetVoucher");
        group.MapPost("/", CreateAsync).RequireAuthorization(AicosPolicies.Management);
        group.MapPost("/{id:guid}/revoke", RevokeAsync).RequireAuthorization(AicosPolicies.Management);
        group.MapPost("/validate", ValidateAsync).RequireAuthorization(AicosPolicies.Staff);
        group.MapPost("/redeem", RedeemAsync).RequireAuthorization(AicosPolicies.Staff);
    }

    private static Task<PagedResponse<VoucherResponse>> ListAsync(
        CustomerEngagementDbContext db, ICurrentUser user, TimeProvider clock, Guid? branchId, VoucherStatus? status, VoucherType? type,
        string? search, int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var branch = BranchAccess.ResolveFilter(user, branchId);
        var query = db.Vouchers.AsNoTracking();
        if (branch is not null)
        {
            query = query.Where(v => v.BranchId == branch);
        }

        if (status is not null)
        {
            query = query.Where(v => v.Status == status);
        }

        if (type is not null)
        {
            query = query.Where(v => v.Type == type);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToUpperInvariant();
            query = query.Where(v => v.Code.Contains(term) || (v.IssuedToName != null && v.IssuedToName.ToUpper().Contains(term)));
        }

        return query.OrderByDescending(v => v.CreatedAtUtc).ToPagedAsync(page, pageSize, v => VoucherResponse.From(v, now), ct);
    }

    private static async Task<VoucherResponse> GetAsync(Guid id, CustomerEngagementDbContext db, ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        var voucher = await db.Vouchers.FirstOrDefaultAsync(v => v.Id == id, ct) ?? throw new NotFoundException("Voucher", id);
        BranchAccess.EnsureCanAccess(user, voucher.BranchId);
        return VoucherResponse.From(voucher, clock.GetUtcNow().UtcDateTime);
    }

    private static async Task<IResult> CreateAsync(
        CreateVoucherRequest r, CustomerEngagementDbContext db, ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        Validate(r, now);
        var branchId = BranchAccess.Resolve(user, r.BranchId);

        var code = string.IsNullOrWhiteSpace(r.Code) ? await NewCodeAsync(db, r.Type!.Value, ct) : r.Code.Trim().ToUpperInvariant();
        if (await db.Vouchers.AnyAsync(v => v.Code == code, ct))
        {
            throw new ConflictException($"Code '{code}' already exists.", "voucher.duplicate_code");
        }

        if (r.IssuedToCustomerId is not null && !await db.Customers.AnyAsync(c => c.Id == r.IssuedToCustomerId, ct))
        {
            throw new ValidationFailedException("IssuedToCustomerId", "Customer does not exist.");
        }

        var discountType = r.Type == VoucherType.GiftCard ? VoucherDiscountType.FixedAmount : r.DiscountType!.Value;
        var voucher = new Voucher(branchId, code, r.Type!.Value, discountType, r.Value!.Value, r.MinOrderAmount, r.ExpiresAt!.Value.UtcDateTime,
            r.IssuedToName, r.IssuedToCustomerId);
        db.Vouchers.Add(voucher);
        await db.SaveChangesAsync(ct);
        return Results.CreatedAtRoute("GetVoucher", new { id = voucher.Id }, VoucherResponse.From(voucher, now));
    }

    private static async Task<VoucherResponse> RevokeAsync(
        Guid id, RevokeVoucherRequest r, CustomerEngagementDbContext db, ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        CommonRules.Required(errors, "Reason", r.Reason, "Reason", 500);
        errors.ThrowIfAny();

        var voucher = await db.Vouchers.FirstOrDefaultAsync(v => v.Id == id, ct) ?? throw new NotFoundException("Voucher", id);
        BranchAccess.EnsureCanAccess(user, voucher.BranchId);
        voucher.Revoke(r.Reason!);
        await db.SaveChangesAsync(ct);
        return VoucherResponse.From(voucher, clock.GetUtcNow().UtcDateTime);
    }

    /// <summary>Preview only (nothing changes): how much this code takes off an order.</summary>
    private static async Task<VoucherQuoteResponse> ValidateAsync(
        ValidateVoucherRequest r, CustomerEngagementDbContext db, ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        ValidateUse(r.Code, r.OrderAmount, orderReference: "-");
        var voucher = await FindByCodeAsync(db, user, r.Code!, ct);
        var discount = voucher.CalculateDiscount(r.OrderAmount!.Value, clock.GetUtcNow().UtcDateTime);
        return new VoucherQuoteResponse(voucher.Code, r.OrderAmount!.Value, discount, r.OrderAmount!.Value - discount);
    }

    private static async Task<VoucherQuoteResponse> RedeemAsync(
        RedeemVoucherRequest r, CustomerEngagementDbContext db, ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        ValidateUse(r.Code, r.OrderAmount, r.OrderReference);
        var voucher = await FindByCodeAsync(db, user, r.Code!, ct);
        var discount = voucher.Redeem(r.OrderAmount!.Value, r.OrderReference!, clock.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct); // Version token: a parallel redeem of the same code fails with 409
        return new VoucherQuoteResponse(voucher.Code, r.OrderAmount!.Value, discount, r.OrderAmount!.Value - discount);
    }

    private static void Validate(CreateVoucherRequest r, DateTime nowUtc)
    {
        var errors = new ValidationErrors();
        if (!string.IsNullOrWhiteSpace(r.Code) && !CodeRegex().IsMatch(r.Code.Trim().ToUpperInvariant()))
        {
            errors.Add("Code", "Code must be 4-30 characters: letters A-Z, digits and '-'.");
        }

        CommonRules.RequiredEnum(errors, "Type", r.Type, "Type");
        if (r.Type == VoucherType.Voucher)
        {
            CommonRules.RequiredEnum(errors, "DiscountType", r.DiscountType, "Discount type");
        }

        if (r.Type == VoucherType.Voucher && r.DiscountType == VoucherDiscountType.Percentage)
        {
            if (r.Value is not (>= 1 and <= 100))
            {
                errors.Add("Value", "Percentage must be between 1 and 100.");
            }
        }
        else
        {
            CommonRules.Positive(errors, "Value", r.Value, "Value", 10_000_000m);
        }

        if (r.MinOrderAmount is not null)
        {
            CommonRules.NonNegative(errors, "MinOrderAmount", r.MinOrderAmount, "Minimum order amount", 100_000_000m);
        }

        if (r.ExpiresAt is null)
        {
            errors.Add("ExpiresAt", "Expiry date is required.");
        }
        else if (r.ExpiresAt.Value.UtcDateTime <= nowUtc)
        {
            errors.Add("ExpiresAt", "Expiry date must be in the future.");
        }
        else if (r.ExpiresAt.Value.UtcDateTime > nowUtc.AddYears(2))
        {
            errors.Add("ExpiresAt", "Expiry date can be at most 2 years ahead.");
        }

        CommonRules.MaxLength(errors, "IssuedToName", r.IssuedToName, "Issued to", 255);
        errors.ThrowIfAny();
    }

    private static void ValidateUse(string? code, decimal? orderAmount, string? orderReference)
    {
        var errors = new ValidationErrors();
        CommonRules.Required(errors, "Code", code, "Code", 30);
        CommonRules.Positive(errors, "OrderAmount", orderAmount, "Order amount", 1_000_000_000m);
        CommonRules.Required(errors, "OrderReference", orderReference, "Order reference", 50);
        errors.ThrowIfAny();
    }

    /// <summary>Unknown codes and codes of other branches look the same to the caller (404).</summary>
    private static async Task<Voucher> FindByCodeAsync(CustomerEngagementDbContext db, ICurrentUser user, string code, CancellationToken ct)
    {
        var normalized = code.Trim().ToUpperInvariant();
        var voucher = await db.Vouchers.FirstOrDefaultAsync(v => v.Code == normalized, ct);
        if (voucher is null || (!user.IsInRole(AicosRoles.BrandAdmin) && user.BranchId != voucher.BranchId))
        {
            throw new NotFoundException("Voucher", normalized);
        }

        return voucher;
    }

    private static async Task<string> NewCodeAsync(CustomerEngagementDbContext db, VoucherType type, CancellationToken ct)
    {
        while (true)
        {
            var code = $"{(type == VoucherType.GiftCard ? "GC" : "VC")}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(4))}";
            if (!await db.Vouchers.AnyAsync(v => v.Code == code, ct))
            {
                return code;
            }
        }
    }

    [GeneratedRegex("^[A-Z0-9-]{4,30}$")]
    private static partial Regex CodeRegex();
}
