using System.Security.Cryptography;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Persistence;
using AICOS.BuildingBlocks.Security;
using AICOS.BuildingBlocks.Validation;
using AICOS.CustomerEngagement.Api.Domain;
using AICOS.CustomerEngagement.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AICOS.CustomerEngagement.Api.Features.Loyalty;

/// <summary>POST .../redemptions. The points to spend; the voucher is worth <c>points x RedeemValuePerPoint</c> of the brand rule.</summary>
public sealed record RedeemPointsRequest(int? Points);

public sealed record RedeemedVoucher(Guid Id, string Code, decimal Value, string DiscountType, DateTime ExpiresAtUtc);

public sealed record PointsRedemptionResponse(RedeemedVoucher Voucher, int PointsSpent, int PointsBalance);

public sealed record MyVoucherResponse(
    Guid Id, string Code, string Type, string DiscountType, decimal Value, decimal? MinOrderAmount, DateTime ExpiresAtUtc, VoucherStatus Status, bool IsExpired, Guid? BranchId);

/// <summary>
/// SCRUM-134 / FR-80 / BR-47 / BR-73: a member turns points into a voucher worth a fixed amount of money, which then discounts an order like
/// any voucher. The points leave the balance in the same save that creates the voucher, so neither can happen without the other. The voucher
/// is issued to that member and only works on their orders. Routes: /api/v1/customers/me/redemptions, /me/vouchers, /{id}/redemptions.
/// </summary>
public static class RedemptionEndpoints
{
    public const int MaxPoints = 100_000;
    private const decimal MaxVoucherValue = 10_000_000m;

    public static void MapRedemptionEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/me/redemptions", RedeemMineAsync).WithTags("Loyalty redemptions").RequireAuthorization(b => b.RequireRole(AicosRoles.Customer));
        api.MapGet("/me/vouchers", MyVouchersAsync).WithTags("Loyalty redemptions").RequireAuthorization(b => b.RequireRole(AicosRoles.Customer));
        api.MapPost("/{id:guid}/redemptions", RedeemForAsync).WithTags("Loyalty redemptions")
            .RequireAuthorization(b => b.RequireRole(AicosRoles.BrandAdmin, AicosRoles.BranchManager, AicosRoles.Cashier));
    }

    private static async Task<IResult> RedeemMineAsync(
        RedeemPointsRequest r, CustomerEngagementDbContext db, LoyaltyRulesService rules, IOptions<LoyaltyOptions> options, ICurrentUser user, TimeProvider clock,
        IEventBus bus, ILoggerFactory loggers, CancellationToken ct)
    {
        var tenantId = TenantScope.Require(user);
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.TenantId == tenantId && c.UserId == user.UserId, ct)
            ?? throw new NotFoundException("Customer profile", user.UserId ?? Guid.Empty);
        return Results.Created("/api/v1/customers/me/vouchers", await RedeemAsync(db, rules, options.Value, customer, r, clock, user, bus, loggers, ct));
    }

    /// <summary>At the counter: the member is present, a cashier or manager spends their points and hands over the code.</summary>
    private static async Task<IResult> RedeemForAsync(
        Guid id, RedeemPointsRequest r, CustomerEngagementDbContext db, LoyaltyRulesService rules, IOptions<LoyaltyOptions> options, ICurrentUser user,
        TimeProvider clock, IEventBus bus, ILoggerFactory loggers, CancellationToken ct)
    {
        var tenantId = TenantScope.Require(user);
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == id && c.TenantId == tenantId, ct) ?? throw new NotFoundException("Customer", id);
        return Results.Created("/api/v1/customers/vouchers", await RedeemAsync(db, rules, options.Value, customer, r, clock, user, bus, loggers, ct));
    }

    private static async Task<PointsRedemptionResponse> RedeemAsync(
        CustomerEngagementDbContext db,
        LoyaltyRulesService rules,
        LoyaltyOptions options,
        Customer customer,
        RedeemPointsRequest r,
        TimeProvider clock,
        ICurrentUser user,
        IEventBus bus,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var errors = new ValidationErrors();
        if (r.Points is null or < 1 or > MaxPoints)
        {
            errors.Add("Points", $"Points must be between 1 and {MaxPoints:N0}.");
        }

        errors.ThrowIfAny();

        var rule = await rules.ForTenantAsync(customer.TenantId, ct);
        if (!rule.IsActive)
        {
            throw new BusinessRuleException("loyalty.rules_inactive", "Points cannot be redeemed: the loyalty program of this brand is switched off.");
        }

        var points = r.Points!.Value;
        if (points < rule.MinPointsToRedeem)
        {
            throw new BusinessRuleException("loyalty.below_minimum", $"At least {rule.MinPointsToRedeem} points are needed for a redemption.");
        }

        var value = points * rule.RedeemValuePerPoint;
        if (value > MaxVoucherValue)
        {
            throw new ValidationFailedException("Points", $"A voucher is worth at most {MaxVoucherValue:N0} VND; redeem fewer points.");
        }

        var before = new { customer.PointsBalance };
        customer.Redeem(points);   // 422 loyalty.insufficient_points

        var now = clock.GetUtcNow().UtcDateTime;
        var code = await NewCodeAsync(db, customer.TenantId, ct);
        var voucher = new Voucher(
            customer.TenantId, null, code, VoucherType.Voucher, VoucherDiscountType.FixedAmount, value, null, now.AddDays(Math.Max(1, options.RedeemedVoucherDays)),
            customer.FullName, customer.Id);
        db.Vouchers.Add(voucher);
        db.LoyaltyTransactions.Add(new LoyaltyTransaction(customer.Id, null, -points, LoyaltyTransactionType.Redeem, $"Redeemed for voucher {code}"));
        await db.SaveChangesAsync(ct);   // one save: the Version token fails a parallel redemption of the same points with 409

        await AuditTrail.PublishAsync(
            bus, user, loggers.CreateLogger("AICOS.Customer.Loyalty"), customer.TenantId, "loyalty.points_redeemed", "Customer", customer.Id, before,
            new { customer.PointsBalance, PointsSpent = points, VoucherCode = code, VoucherValue = value });

        return new PointsRedemptionResponse(
            new RedeemedVoucher(voucher.Id, code, value, voucher.DiscountType.ToString(), voucher.ExpiresAtUtc), points, customer.PointsBalance);
    }

    /// <summary>The vouchers issued to the signed-in member (bought with points or given by the brand), newest first.</summary>
    private static async Task<PagedResponse<MyVoucherResponse>> MyVouchersAsync(
        CustomerEngagementDbContext db, ICurrentUser user, TimeProvider clock, VoucherStatus? status, int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        var tenantId = TenantScope.Require(user);
        var customer = await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == tenantId && c.UserId == user.UserId, ct)
            ?? throw new NotFoundException("Customer profile", user.UserId ?? Guid.Empty);
        var now = clock.GetUtcNow().UtcDateTime;

        var query = db.Vouchers.AsNoTracking().Where(v => v.TenantId == tenantId && v.IssuedToCustomerId == customer.Id);
        if (status is not null)
        {
            query = query.Where(v => v.Status == status);
        }

        return await query.OrderByDescending(v => v.CreatedAtUtc).ToPagedAsync(
            page, pageSize,
            v => new MyVoucherResponse(v.Id, v.Code, v.Type.ToString(), v.DiscountType.ToString(), v.Value, v.MinOrderAmount, v.ExpiresAtUtc, v.Status, v.IsExpired(now), v.BranchId), ct);
    }

    private static async Task<string> NewCodeAsync(CustomerEngagementDbContext db, Guid tenantId, CancellationToken ct)
    {
        while (true)
        {
            var code = $"PT-{Convert.ToHexString(RandomNumberGenerator.GetBytes(4))}";
            if (!await db.Vouchers.AnyAsync(v => v.TenantId == tenantId && v.Code == code, ct))
            {
                return code;
            }
        }
    }
}
