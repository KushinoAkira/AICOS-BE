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
/// <see cref="RequiredOrders"/> makes a voucher customers hunt for: a member with that many paid orders (each at least
/// <see cref="RequiredOrderAmount"/>, paid from <see cref="CountOrdersFrom"/>, default now) claims it and gets a personal code.
/// </summary>
public sealed record CreateVoucherRequest(
    Guid? BranchId, string? Code, VoucherType? Type, VoucherDiscountType? DiscountType, decimal? Value, decimal? MinOrderAmount,
    DateTimeOffset? ExpiresAt, string? IssuedToName, Guid? IssuedToCustomerId,
    int? RequiredOrders = null, decimal? RequiredOrderAmount = null, DateTimeOffset? CountOrdersFrom = null);

public sealed record RevokeVoucherRequest(string? Reason);

/// <summary><c>BranchId</c> and <c>CustomerId</c> (the customer profile id or user id) of the order let a voucher of another branch or another member be refused.</summary>
public sealed record ValidateVoucherRequest(string? Code, decimal? OrderAmount, Guid? BranchId = null, Guid? CustomerId = null);

public sealed record RedeemVoucherRequest(string? Code, decimal? OrderAmount, string? OrderReference, Guid? BranchId = null, Guid? CustomerId = null);

/// <summary><c>ClaimedCount</c>: for a hunt voucher, how many members claimed it.</summary>
public sealed record VoucherResponse(
    Guid Id, Guid? BranchId, string Code, VoucherType Type, VoucherDiscountType DiscountType, decimal Value, decimal? MinOrderAmount,
    DateTime ExpiresAtUtc, bool IsExpired, VoucherStatus Status, string? IssuedToName, Guid? IssuedToCustomerId,
    DateTime? RedeemedAtUtc, string? RedeemedOrderReference, decimal? RedeemedDiscount, string? RevokeReason, DateTime CreatedAtUtc,
    int? RequiredOrders = null, decimal? RequiredOrderAmount = null, DateTime? CountOrdersFromUtc = null, Guid? CampaignId = null, int ClaimedCount = 0)
{
    public static VoucherResponse From(Voucher v, DateTime nowUtc, int claimed = 0) => new(
        v.Id, v.BranchId, v.Code, v.Type, v.DiscountType, v.Value, v.MinOrderAmount, v.ExpiresAtUtc, v.IsExpired(nowUtc), v.Status,
        v.IssuedToName, v.IssuedToCustomerId, v.RedeemedAtUtc, v.RedeemedOrderReference, v.RedeemedDiscount, v.RevokeReason, v.CreatedAtUtc,
        v.RequiredOrders, v.RequiredOrderAmount, v.CountOrdersFromUtc, v.CampaignId, claimed);
}

/// <summary>A voucher a member can hunt for, with their progress: <c>CountedOrders</c> of <c>RequiredOrders</c>, and their code once claimed.</summary>
public sealed record HuntVoucherResponse(
    Guid Id, string Code, VoucherDiscountType DiscountType, decimal Value, decimal? MinOrderAmount, DateTime ExpiresAtUtc, Guid? BranchId,
    int RequiredOrders, decimal? RequiredOrderAmount, DateTime CountOrdersFromUtc, int CountedOrders, bool Claimed, string? ClaimedCode, bool CanClaim);

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
        // Staff apply a code at the counter; a member applies one to their own order (takeaway, table QR), which Ordering forwards with their token.
        group.MapPost("/validate", ValidateAsync).RequireAuthorization(b => b.RequireRole(VoucherUsers));
        group.MapPost("/redeem", RedeemAsync).RequireAuthorization(b => b.RequireRole(VoucherUsers));
        // Hunting: a member sees the vouchers they can earn with orders and claims one once they qualify.
        group.MapGet("/hunt", HuntListAsync).RequireAuthorization(b => b.RequireRole(AicosRoles.Customer));
        group.MapPost("/hunt/{id:guid}/claim", ClaimAsync).RequireAuthorization(b => b.RequireRole(AicosRoles.Customer));
    }

    private static readonly string[] VoucherUsers =
        [AicosRoles.BrandAdmin, AicosRoles.BranchManager, AicosRoles.Cashier, AicosRoles.Barista, AicosRoles.Customer];

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

    private static async Task<PagedResponse<VoucherResponse>> ListAsync(
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

        var paged = await query.OrderByDescending(v => v.CreatedAtUtc).ToPagedAsync(page, pageSize, v => v, ct);
        var hunts = paged.Items.Where(v => v.IsHunt).Select(v => (Guid?)v.Id).ToList();
        var claims = hunts.Count == 0
            ? new Dictionary<Guid, int>()
            : (await db.Vouchers.AsNoTracking().Where(v => v.CampaignId != null && hunts.Contains(v.CampaignId)).Select(v => v.CampaignId!.Value).ToListAsync(ct))
                .GroupBy(c => c).ToDictionary(g => g.Key, g => g.Count());
        return new PagedResponse<VoucherResponse>(
            paged.Items.Select(v => VoucherResponse.From(v, now, claims.GetValueOrDefault(v.Id))).ToList(), paged.Page, paged.PageSize, paged.TotalCount);
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
        if (r.RequiredOrders is not null)
        {
            voucher.MakeHunt(r.RequiredOrders.Value, r.RequiredOrderAmount, r.CountOrdersFrom?.UtcDateTime ?? now);
        }

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
        await EnsureUsableByAsync(db, voucher, r.BranchId, CustomerOf(user, r.CustomerId), user, ct);
        var discount = voucher.CalculateDiscount(r.OrderAmount!.Value, clock.GetUtcNow().UtcDateTime);
        return new VoucherQuoteResponse(voucher.Code, r.OrderAmount!.Value, discount, r.OrderAmount!.Value - discount);
    }

    private static async Task<VoucherQuoteResponse> RedeemAsync(
        RedeemVoucherRequest r, CustomerEngagementDbContext db, ICurrentUser user, TimeProvider clock, IEventBus bus, ILoggerFactory loggers, CancellationToken ct)
    {
        ValidateUse(r.Code, r.OrderAmount, r.OrderReference);
        var voucher = await FindByCodeAsync(db, user, r.Code!, ct);
        await EnsureUsableByAsync(db, voucher, r.BranchId, CustomerOf(user, r.CustomerId), user, ct);
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
        if (r.RequiredOrders is not null)
        {
            if (r.RequiredOrders is < 1 or > 1000)
            {
                errors.Add("RequiredOrders", "Required orders must be between 1 and 1000.");
            }

            if (r.Type != VoucherType.Voucher)
            {
                errors.Add("Type", "Only a discount voucher can be hunted.");
            }

            if (r.IssuedToCustomerId is not null || !string.IsNullOrWhiteSpace(r.IssuedToName))
            {
                errors.Add("IssuedToCustomerId", "A hunt voucher is for every member; do not issue it to one person.");
            }

            if (!string.IsNullOrWhiteSpace(r.Code) && r.Code.Trim().Length > MaxHuntCodeLength)
            {
                errors.Add("Code", $"A hunt voucher code has at most {MaxHuntCodeLength} characters (each member's code adds 5).");
            }

            if (r.RequiredOrderAmount is not null)
            {
                CommonRules.NonNegative(errors, "RequiredOrderAmount", r.RequiredOrderAmount, "Required order amount", 100_000_000m);
            }

            if (r.CountOrdersFrom is not null && r.ExpiresAt is not null && r.CountOrdersFrom.Value >= r.ExpiresAt.Value)
            {
                errors.Add("CountOrdersFrom", "Orders must be counted from a date before the expiry.");
            }
        }
        else if (r.RequiredOrderAmount is not null || r.CountOrdersFrom is not null)
        {
            errors.Add("RequiredOrders", "Give the number of orders needed to hunt this voucher.");
        }

        errors.ThrowIfAny();
    }

    private const int MaxHuntCodeLength = 25;

    /// <summary>The hunt vouchers of the brand still open (active, not expired), each with the caller's progress.</summary>
    private static async Task<IReadOnlyList<HuntVoucherResponse>> HuntListAsync(CustomerEngagementDbContext db, ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        var tenantId = TenantScope.Require(user);
        var now = clock.GetUtcNow().UtcDateTime;
        var hunts = await db.Vouchers.AsNoTracking()
            .Where(v => v.TenantId == tenantId && v.RequiredOrders != null && v.Status == VoucherStatus.Active && v.ExpiresAtUtc > now)
            .OrderBy(v => v.RequiredOrders).ThenBy(v => v.ExpiresAtUtc).ToListAsync(ct);
        var customer = await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == tenantId && c.UserId == user.UserId, ct);
        var orders = customer is null ? [] : await db.MemberOrders.AsNoTracking().Where(o => o.CustomerId == customer.Id && !o.IsCancelled).ToListAsync(ct);
        var ids = hunts.Select(h => (Guid?)h.Id).ToList();
        var mine = customer is null
            ? new Dictionary<Guid, string>()
            : await db.Vouchers.AsNoTracking().Where(v => v.IssuedToCustomerId == customer.Id && ids.Contains(v.CampaignId))
                .ToDictionaryAsync(v => v.CampaignId!.Value, v => v.Code, ct);

        return hunts.Select(h =>
        {
            var counted = Counted(h, orders);
            var claimed = mine.TryGetValue(h.Id, out var code);
            return new HuntVoucherResponse(
                h.Id, h.Code, h.DiscountType, h.Value, h.MinOrderAmount, h.ExpiresAtUtc, h.BranchId, h.RequiredOrders!.Value, h.RequiredOrderAmount,
                h.CountOrdersFromUtc ?? h.CreatedAtUtc, counted, claimed, claimed ? code : null, customer is not null && !claimed && counted >= h.RequiredOrders);
        }).ToList();
    }

    private static int Counted(Voucher hunt, IEnumerable<MemberOrder> orders) =>
        orders.Count(o => o.PaidAtUtc >= (hunt.CountOrdersFromUtc ?? hunt.CreatedAtUtc) && o.Amount >= (hunt.RequiredOrderAmount ?? 0));

    /// <summary>A member who has the orders claims the hunt voucher: they get a personal single-use code, once per voucher. Audited.</summary>
    private static async Task<IResult> ClaimAsync(
        Guid id, CustomerEngagementDbContext db, ICurrentUser user, TimeProvider clock, IEventBus bus, ILoggerFactory loggers, CancellationToken ct)
    {
        var tenantId = TenantScope.Require(user);
        var now = clock.GetUtcNow().UtcDateTime;
        var customer = await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == tenantId && c.UserId == user.UserId, ct)
            ?? throw new NotFoundException("Customer profile", user.UserId ?? Guid.Empty);
        var hunt = await db.Vouchers.FirstOrDefaultAsync(v => v.Id == id && v.TenantId == tenantId && v.RequiredOrders != null, ct)
            ?? throw new NotFoundException("Voucher", id);
        if (hunt.Status != VoucherStatus.Active || hunt.IsExpired(now))
        {
            throw new BusinessRuleException("voucher.hunt_closed", "This voucher can no longer be claimed.");
        }

        if (await db.Vouchers.AnyAsync(v => v.CampaignId == hunt.Id && v.IssuedToCustomerId == customer.Id, ct))
        {
            throw new ConflictException("You already claimed this voucher.", "voucher.hunt_claimed");
        }

        var orders = await db.MemberOrders.AsNoTracking().Where(o => o.CustomerId == customer.Id && !o.IsCancelled).ToListAsync(ct);
        var counted = Counted(hunt, orders);
        if (counted < hunt.RequiredOrders)
        {
            throw new BusinessRuleException("voucher.hunt_not_eligible", $"You have {counted} of the {hunt.RequiredOrders} orders this voucher needs.");
        }

        string code;
        do
        {
            code = $"{hunt.Code}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(2))}";
        }
        while (await db.Vouchers.AnyAsync(v => v.TenantId == tenantId && v.Code == code, ct));

        var claim = hunt.IssueClaim(code, customer.Id, customer.FullName);
        db.Vouchers.Add(claim);
        await db.SaveChangesAsync(ct);   // the unique (campaign, member) index stops a double claim made at the same moment

        await AuditTrail.PublishAsync(bus, user, loggers.CreateLogger("AICOS.Customer.Vouchers"), tenantId, "voucher.claimed", "Voucher", claim.Id, after: Snapshot(claim));
        return Results.Created("/api/v1/customers/me/vouchers", VoucherResponse.From(claim, now));
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
        // A member has no branch of their own: the branch of the order is checked in EnsureUsableByAsync instead.
        if (voucher is null || (voucher.BranchId is not null && !user.IsInRole(AicosRoles.BrandAdmin) && !user.IsInRole(AicosRoles.Customer) && user.BranchId != voucher.BranchId))
        {
            throw new NotFoundException("Voucher", normalized);
        }

        return voucher;
    }

    /// <summary>
    /// A voucher valid at one branch is refused at another, and a voucher issued to a member (for example one bought with points) can only be
    /// used on that member's order (SCRUM-133, SCRUM-134). The member may be named by profile id or by account id.
    /// </summary>
    private static async Task EnsureUsableByAsync(
        CustomerEngagementDbContext db, Voucher voucher, Guid? branchId, Guid? customerId, ICurrentUser user, CancellationToken ct)
    {
        if (voucher.BranchId is not null && (branchId is null ? user.IsInRole(AicosRoles.Customer) : voucher.BranchId != branchId))
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

    /// <summary>A member always uses a code as themselves: the customer named in the request only counts for staff.</summary>
    private static Guid? CustomerOf(ICurrentUser user, Guid? requested) => user.IsInRole(AicosRoles.Customer) ? user.UserId : requested;

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
