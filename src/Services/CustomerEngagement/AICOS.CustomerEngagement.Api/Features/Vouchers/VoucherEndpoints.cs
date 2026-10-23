using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Persistence;
using AICOS.BuildingBlocks.Security;
using AICOS.BuildingBlocks.Validation;
using AICOS.CustomerEngagement.Api.Domain;
using AICOS.CustomerEngagement.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.CustomerEngagement.Api.Features.Vouchers;

/// <summary>
/// <see cref="Code"/> is optional (generated when empty). <see cref="BranchId"/> is optional: empty means valid at every branch of
/// the brand. Gift cards are always a fixed amount.
/// </summary>
public sealed record CreateVoucherRequest(
    Guid? BranchId, string? Code, VoucherType? Type, VoucherDiscountType? DiscountType, decimal? Value, decimal? MinOrderAmount,
    DateTimeOffset? ExpiresAt, string? IssuedToName, Guid? IssuedToCustomerId);

public sealed record RevokeVoucherRequest(string? Reason);

/// <summary><c>BranchId</c> and <c>CustomerId</c> (the customer profile id or user id) of the order let a voucher of another branch or another member be refused.</summary>
public sealed record ValidateVoucherRequest(string? Code, decimal? OrderAmount, Guid? BranchId = null, Guid? CustomerId = null);

public sealed record RedeemVoucherRequest(string? Code, decimal? OrderAmount, string? OrderReference, Guid? BranchId = null, Guid? CustomerId = null);

public sealed record VoucherResponse(
    Guid Id, Guid? BranchId, string Code, VoucherType Type, VoucherDiscountType DiscountType, decimal Value, decimal? MinOrderAmount,
    DateTime ExpiresAtUtc, bool IsExpired, VoucherStatus Status, string? IssuedToName, Guid? IssuedToCustomerId,
    DateTime? RedeemedAtUtc, string? RedeemedOrderReference, decimal? RedeemedDiscount, string? RevokeReason, DateTime CreatedAtUtc)
{
    public static VoucherResponse From(Voucher v, DateTime nowUtc) => new(
        v.Id, v.BranchId, v.Code, v.Type, v.DiscountType, v.Value, v.MinOrderAmount, v.ExpiresAtUtc, v.IsExpired(nowUtc), v.Status,
        v.IssuedToName, v.IssuedToCustomerId, v.RedeemedAtUtc, v.RedeemedOrderReference, v.RedeemedDiscount, v.RevokeReason, v.CreatedAtUtc);
}

public sealed record VoucherQuoteResponse(string Code, decimal OrderAmount, decimal Discount, decimal AmountToPay);

/// <summary>
/// UC-17 Manage Vouchers. Routes: /api/v1/customers/vouchers. Issue and revoke: BrandAdmin only. List: BrandAdmin (whole brand) and
/// BranchManager (vouchers valid at their branch). Validate (preview) and redeem at the counter: staff of the brand, for vouchers that
/// are valid at their branch. BR-46: expired, redeemed or revoked codes are rejected (422).
/// </summary>
public static partial class VoucherEndpoints
{
    public static void MapVoucherEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/vouchers").WithTags("Vouchers");

        group.MapGet("/", ListAsync).RequireAuthorization(AicosPolicies.Management);
        group.MapGet("/{id:guid}", GetAsync).RequireAuthorization(AicosPolicies.Management).WithName("GetVoucher");
        group.MapPost("/", CreateAsync).RequireAuthorization(AicosPolicies.AdminOnly);
        group.MapPost("/{id:guid}/revoke", RevokeAsync).RequireAuthorization(AicosPolicies.AdminOnly);
        group.MapPost("/validate", ValidateAsync).RequireAuthorization(AicosPolicies.Staff);
        group.MapPost("/redeem", RedeemAsync).RequireAuthorization(AicosPolicies.Staff);
    }

    /// <summary>The vouchers the caller may see: the brand, narrowed for a BranchManager to the ones valid at their branch.</summary>
    private static IQueryable<Voucher> Scope(CustomerEngagementDbContext db, ICurrentUser user)
    {
        var tenantId = TenantScope.Require(user);
        var query = db.Vouchers.Where(v => v.TenantId == tenantId);
        if (user.IsInRole(AicosRoles.BranchManager))
        {
            var own = user.BranchId ?? throw new ForbiddenException("Your account is not assigned to a branch.");
            query = query.Where(v => v.BranchId == null || v.BranchId == own);
        }

        return query;
    }

    private static Task<PagedResponse<VoucherResponse>> ListAsync(
        CustomerEngagementDbContext db, ICurrentUser user, TimeProvider clock, Guid? branchId, VoucherStatus? status, VoucherType? type,
        string? search, int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var query = Scope(db, user).AsNoTracking();
        if (branchId is not null)
        {
            query = query.Where(v => v.BranchId == branchId);
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

    private static async Task<VoucherResponse> GetAsync(Guid id, CustomerEngagementDbContext db, ICurrentUser user, TimeProvider clock, CancellationToken ct) =>
        VoucherResponse.From(await FindAsync(db, user, id, ct), clock.GetUtcNow().UtcDateTime);

    private static async Task<IResult> CreateAsync(
        CreateVoucherRequest r,
        CustomerEngagementDbContext db,
        ICurrentUser user,
        IBranchDirectory directory,
        TimeProvider clock,
        IEventBus bus,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        Validate(r, now);
        var tenantId = TenantScope.Require(user);
        if (r.BranchId is not null)
        {
            await BranchScope.EnsureUsableAsync(directory, r.BranchId.Value, requireActive: false, ct);
        }

        var code = string.IsNullOrWhiteSpace(r.Code) ? await NewCodeAsync(db, tenantId, r.Type!.Value, ct) : r.Code.Trim().ToUpperInvariant();
        if (await db.Vouchers.AnyAsync(v => v.TenantId == tenantId && v.Code == code, ct))
        {
            throw new ConflictException($"Code '{code}' already exists.", "voucher.duplicate_code");
        }

        if (r.IssuedToCustomerId is not null && !await db.Customers.AnyAsync(c => c.Id == r.IssuedToCustomerId && c.TenantId == tenantId, ct))
        {
            throw new ValidationFailedException("IssuedToCustomerId", "Customer does not exist.");
        }

        var discountType = r.Type == VoucherType.GiftCard ? VoucherDiscountType.FixedAmount : r.DiscountType!.Value;
        var voucher = new Voucher(
            tenantId, r.BranchId, code, r.Type!.Value, discountType, r.Value!.Value, r.MinOrderAmount, r.ExpiresAt!.Value.UtcDateTime, r.IssuedToName, r.IssuedToCustomerId);
        db.Vouchers.Add(voucher);
        await db.SaveChangesAsync(ct);

        await AuditTrail.PublishAsync(bus, user, loggers.CreateLogger("AICOS.Customer.Vouchers"), tenantId, "voucher.created", "Voucher", voucher.Id, after: Snapshot(voucher));
        return Results.CreatedAtRoute("GetVoucher", new { id = voucher.Id }, VoucherResponse.From(voucher, now));
    }

    private static async Task<VoucherResponse> RevokeAsync(
        Guid id, RevokeVoucherRequest r, CustomerEngagementDbContext db, ICurrentUser user, TimeProvider clock, IEventBus bus, ILoggerFactory loggers, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        CommonRules.Required(errors, "Reason", r.Reason, "Reason", 500);
        errors.ThrowIfAny();

        var voucher = await FindAsync(db, user, id, ct);
        var before = Snapshot(voucher);
        voucher.Revoke(r.Reason!);
        await db.SaveChangesAsync(ct);

        await AuditTrail.PublishAsync(
            bus, user, loggers.CreateLogger("AICOS.Customer.Vouchers"), voucher.TenantId, "voucher.revoked", "Voucher", voucher.Id, before, Snapshot(voucher));
        return VoucherResponse.From(voucher, clock.GetUtcNow().UtcDateTime);
    }

    /// <summary>Preview only (nothing changes): how much this code takes off an order.</summary>
    private static async Task<VoucherQuoteResponse> ValidateAsync(
        ValidateVoucherRequest r, CustomerEngagementDbContext db, ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        ValidateUse(r.Code, r.OrderAmount, orderReference: "-");
        var voucher = await FindByCodeAsync(db, user, r.Code!, ct);
        await EnsureUsableByAsync(db, voucher, r.BranchId, r.CustomerId, ct);
        var discount = voucher.CalculateDiscount(r.OrderAmount!.Value, clock.GetUtcNow().UtcDateTime);
        return new VoucherQuoteResponse(voucher.Code, r.OrderAmount!.Value, discount, r.OrderAmount!.Value - discount);
    }

    private static async Task<VoucherQuoteResponse> RedeemAsync(
        RedeemVoucherRequest r, CustomerEngagementDbContext db, ICurrentUser user, TimeProvider clock, IEventBus bus, ILoggerFactory loggers, CancellationToken ct)
    {
        ValidateUse(r.Code, r.OrderAmount, r.OrderReference);
        var voucher = await FindByCodeAsync(db, user, r.Code!, ct);
        await EnsureUsableByAsync(db, voucher, r.BranchId, r.CustomerId, ct);
        var before = Snapshot(voucher);
        var discount = voucher.Redeem(r.OrderAmount!.Value, r.OrderReference!, clock.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct); // Version token: a parallel redeem of the same code fails with 409

        await AuditTrail.PublishAsync(
            bus, user, loggers.CreateLogger("AICOS.Customer.Vouchers"), voucher.TenantId, "voucher.redeemed", "Voucher", voucher.Id, before, Snapshot(voucher));
        return new VoucherQuoteResponse(voucher.Code, r.OrderAmount!.Value, discount, r.OrderAmount!.Value - discount);
    }

    private static object Snapshot(Voucher v) => new
    {
        v.Code, Type = v.Type.ToString(), DiscountType = v.DiscountType.ToString(), v.Value, v.MinOrderAmount, v.BranchId, v.ExpiresAtUtc,
        Status = v.Status.ToString(), v.RedeemedOrderReference, v.RedeemedDiscount,
    };

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

    /// <summary>A voucher of another brand, or one valid only at another branch, looks the same as an unknown code (404).</summary>
    private static async Task<Voucher> FindAsync(CustomerEngagementDbContext db, ICurrentUser user, Guid id, CancellationToken ct) =>
        await Scope(db, user).FirstOrDefaultAsync(v => v.Id == id, ct) ?? throw new NotFoundException("Voucher", id);

    private static async Task<Voucher> FindByCodeAsync(CustomerEngagementDbContext db, ICurrentUser user, string code, CancellationToken ct)
    {
        var tenantId = TenantScope.Require(user);
        var normalized = code.Trim().ToUpperInvariant();
        var voucher = await db.Vouchers.FirstOrDefaultAsync(v => v.TenantId == tenantId && v.Code == normalized, ct);
        if (voucher is null || (voucher.BranchId is not null && !user.IsInRole(AicosRoles.BrandAdmin) && user.BranchId != voucher.BranchId))
        {
            throw new NotFoundException("Voucher", normalized);
        }

        return voucher;
    }

    /// <summary>
    /// A voucher valid at one branch is refused at another, and a voucher issued to a member (for example one bought with points) can only be
    /// used on that member's order (SCRUM-133, SCRUM-134). The member may be named by profile id or by account id.
    /// </summary>
    private static async Task EnsureUsableByAsync(CustomerEngagementDbContext db, Voucher voucher, Guid? branchId, Guid? customerId, CancellationToken ct)
    {
        if (voucher.BranchId is not null && branchId is not null && voucher.BranchId != branchId)
        {
            throw new BusinessRuleException("voucher.wrong_branch", "This code is not valid at this branch.");
        }

        if (voucher.IssuedToCustomerId is null)
        {
            return;
        }

        var owner = customerId is null
            ? null
            : await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == voucher.TenantId && (c.Id == customerId || c.UserId == customerId), ct);
        if (owner is null || owner.Id != voucher.IssuedToCustomerId)
        {
            throw new BusinessRuleException("voucher.not_yours", "This code was issued to another member.");
        }
    }

    private static async Task<string> NewCodeAsync(CustomerEngagementDbContext db, Guid tenantId, VoucherType type, CancellationToken ct)
    {
        while (true)
        {
            var code = $"{(type == VoucherType.GiftCard ? "GC" : "VC")}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(4))}";
            if (!await db.Vouchers.AnyAsync(v => v.TenantId == tenantId && v.Code == code, ct))
            {
                return code;
            }
        }
    }

    [GeneratedRegex("^[A-Z0-9-]{4,30}$")]
    private static partial Regex CodeRegex();
}
