using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Persistence;
using AICOS.BuildingBlocks.Security;
using AICOS.BuildingBlocks.Validation;
using AICOS.CustomerEngagement.Api.Domain;
using AICOS.CustomerEngagement.Api.Features.Loyalty;
using AICOS.CustomerEngagement.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.CustomerEngagement.Api.Features.Customers;

public sealed record UpdateProfileRequest(string? FullName, string? Phone, DateOnly? Birthday, bool? MarketingOptIn);

/// <summary>PUT /customers/me/preferences: replaces the whole preference set. Sugar is a percentage (0-100); Ice is NoIce, Less or Normal.</summary>
public sealed record PreferencesRequest(int? SugarPercent, string? Ice, List<string>? FavoriteToppings, string? Notes);

public sealed record PreferencesResponse(int? SugarPercent, string? Ice, IReadOnlyList<string> FavoriteToppings, string? Notes)
{
    public static PreferencesResponse From(Customer c) => new(c.PreferredSugarPercent, c.PreferredIce, c.FavoriteToppings, c.PreferenceNotes);
}

public sealed record PointsAdjustmentRequest(int? Points, string? Note);

public sealed record CustomerResponse(
    Guid Id, Guid UserId, string FullName, string Email, string? Phone, DateOnly? Birthday, bool MarketingOptIn,
    int PointsBalance, int LifetimePoints, LoyaltyTier Tier, DateTime CreatedAtUtc)
{
    public static CustomerResponse From(Customer c) => new(
        c.Id, c.UserId, c.FullName, c.Email, c.Phone, c.Birthday, c.MarketingOptIn, c.PointsBalance, c.LifetimePoints, c.Tier, c.CreatedAtUtc);
}

public sealed record LoyaltyTransactionResponse(Guid Id, Guid? OrderId, int Points, LoyaltyTransactionType Type, string? Note, DateTime CreatedAtUtc);

public sealed record LoyaltySummaryResponse(
    int PointsBalance, int LifetimePoints, LoyaltyTier Tier, int? NextTierAt, int? PointsToNextTier, PagedResponse<LoyaltyTransactionResponse> History);

/// <summary>
/// UC-44 Manage Customer Profile, UC-30 View Loyalty Points. Routes: /api/v1/customers.
/// /me*: the signed-in Customer. Lookup by phone/name: all staff (POS). Manual points adjustment: BrandAdmin/Manager.
/// </summary>
public static class CustomerEndpoints
{
    public static void MapCustomerEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/me", MeAsync).WithTags("Customers").RequireAuthorization();
        api.MapPut("/me", UpdateMeAsync).WithTags("Customers").RequireAuthorization();
        api.MapGet("/me/loyalty", MyLoyaltyAsync).WithTags("Customers").RequireAuthorization();
        api.MapGet("/me/preferences", MyPreferencesAsync).WithTags("Customers").RequireAuthorization();
        api.MapPut("/me/preferences", UpdateMyPreferencesAsync).WithTags("Customers").RequireAuthorization();

        api.MapGet("/", ListAsync).WithTags("Customers").RequireAuthorization(AicosPolicies.Staff);
        api.MapGet("/{id:guid}", GetAsync).WithTags("Customers").RequireAuthorization(AicosPolicies.Staff);
        api.MapGet("/{id:guid}/loyalty", LoyaltyAsync).WithTags("Customers").RequireAuthorization(AicosPolicies.Staff);
        api.MapPost("/{id:guid}/points-adjustments", AdjustPointsAsync).WithTags("Customers").RequireAuthorization(AicosPolicies.Management);
    }

    private static async Task<CustomerResponse> MeAsync(CustomerEngagementDbContext db, ICurrentUser user, CancellationToken ct) =>
        CustomerResponse.From(await CurrentAsync(db, user, ct));

    private static async Task<CustomerResponse> UpdateMeAsync(
        UpdateProfileRequest r, CustomerEngagementDbContext db, ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        CommonRules.Required(errors, "FullName", r.FullName, "Full name", 255);
        CommonRules.Phone(errors, "Phone", r.Phone);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        if (r.Birthday is not null && (r.Birthday > today || r.Birthday < new DateOnly(1900, 1, 1)))
        {
            errors.Add("Birthday", "Birthday must be a real date in the past.");
        }

        errors.ThrowIfAny();

        var customer = await CurrentAsync(db, user, ct);
        customer.UpdateProfile(r.FullName!, r.Phone, r.Birthday, r.MarketingOptIn ?? customer.MarketingOptIn);
        await db.SaveChangesAsync(ct);
        return CustomerResponse.From(customer);
    }

    private static readonly string[] IceLevels = ["NoIce", "Less", "Normal"];

    private static async Task<PreferencesResponse> MyPreferencesAsync(CustomerEngagementDbContext db, ICurrentUser user, CancellationToken ct) =>
        PreferencesResponse.From(await CurrentAsync(db, user, ct));

    /// <summary>UC-03: declared drink preferences, the fallback input for recommendations when there is no purchase history (BR-38).</summary>
    private static async Task<PreferencesResponse> UpdateMyPreferencesAsync(
        PreferencesRequest r, CustomerEngagementDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        if (r.SugarPercent is < 0 or > 100)
        {
            errors.Add("SugarPercent", "Sugar level must be between 0 and 100 percent.");
        }

        var ice = IceLevels.FirstOrDefault(l => string.Equals(l, r.Ice?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(r.Ice) && ice is null)
        {
            errors.Add("Ice", $"Ice must be one of: {string.Join(", ", IceLevels)}.");
        }

        var toppings = (r.FavoriteToppings ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList();
        if (toppings.Count > 10)
        {
            errors.Add("FavoriteToppings", "At most 10 favourite toppings.");
        }

        if (toppings.Any(t => t.Length > 50))
        {
            errors.Add("FavoriteToppings", "Each topping name must be at most 50 characters.");
        }

        if (toppings.Distinct(StringComparer.OrdinalIgnoreCase).Count() != toppings.Count)
        {
            errors.Add("FavoriteToppings", "Toppings must not repeat.");
        }

        CommonRules.MaxLength(errors, "Notes", r.Notes, "Notes", 500);
        errors.ThrowIfAny();

        var customer = await CurrentAsync(db, user, ct);
        customer.SetPreferences(r.SugarPercent, ice, toppings, r.Notes);
        await db.SaveChangesAsync(ct);
        return PreferencesResponse.From(customer);
    }

    private static async Task<LoyaltySummaryResponse> MyLoyaltyAsync(
        CustomerEngagementDbContext db, ICurrentUser user, LoyaltyRulesService rules, int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        var customer = await CurrentAsync(db, user, ct);
        return await SummaryAsync(db, customer, (await rules.ForTenantAsync(customer.TenantId, ct)).Thresholds, page, pageSize, ct);
    }

    /// <summary>POS lookup: search by phone, name or email, among the customers of the staff member own brand (BR-04).</summary>
    private static Task<PagedResponse<CustomerResponse>> ListAsync(
        CustomerEngagementDbContext db, ICurrentUser user, string? search, LoyaltyTier? tier, int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        var tenantId = TenantScope.Require(user);
        var query = db.Customers.AsNoTracking().Where(c => c.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(c => c.FullName.ToLower().Contains(term) || c.Email.Contains(term) || (c.Phone != null && c.Phone.Contains(term)));
        }

        if (tier is not null)
        {
            query = query.Where(c => c.Tier == tier);
        }

        return query.OrderBy(c => c.FullName).ToPagedAsync(page, pageSize, CustomerResponse.From, ct);
    }

    private static async Task<CustomerResponse> GetAsync(Guid id, CustomerEngagementDbContext db, ICurrentUser user, CancellationToken ct) =>
        CustomerResponse.From(await FindAsync(db, user, id, ct));

    private static async Task<LoyaltySummaryResponse> LoyaltyAsync(
        Guid id, CustomerEngagementDbContext db, ICurrentUser user, LoyaltyRulesService rules, int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        var customer = await FindAsync(db, user, id, ct);
        return await SummaryAsync(db, customer, (await rules.ForTenantAsync(customer.TenantId, ct)).Thresholds, page, pageSize, ct);
    }

    /// <summary>A manual correction. It is recorded in the points history and in the audit history (FR-67).</summary>
    private static async Task<CustomerResponse> AdjustPointsAsync(
        Guid id,
        PointsAdjustmentRequest r,
        CustomerEngagementDbContext db,
        ICurrentUser user,
        LoyaltyRulesService rules,
        IEventBus bus,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var errors = new ValidationErrors();
        if (r.Points is null or 0 or < -100_000 or > 100_000)
        {
            errors.Add("Points", "Points must be a non-zero number between -100000 and 100000.");
        }

        CommonRules.Required(errors, "Note", r.Note, "Reason for the adjustment", 500);
        errors.ThrowIfAny();

        var customer = await FindAsync(db, user, id, ct);
        var before = new { customer.PointsBalance, customer.LifetimePoints, Tier = customer.Tier.ToString() };

        customer.Adjust(r.Points!.Value, (await rules.ForTenantAsync(customer.TenantId, ct)).Thresholds);
        db.LoyaltyTransactions.Add(new LoyaltyTransaction(customer.Id, null, r.Points.Value, LoyaltyTransactionType.Adjust, r.Note!.Trim()));
        await db.SaveChangesAsync(ct);

        await AuditTrail.PublishAsync(
            bus, user, loggers.CreateLogger("AICOS.Customer.Points"), customer.TenantId, "points.adjusted", "Customer", customer.Id, before,
            new { customer.PointsBalance, customer.LifetimePoints, Tier = customer.Tier.ToString(), Points = r.Points.Value, Reason = r.Note!.Trim() });
        return CustomerResponse.From(customer);
    }

    private static async Task<LoyaltySummaryResponse> SummaryAsync(
        CustomerEngagementDbContext db, Customer customer, LoyaltyTierThresholds thresholds, int page, int pageSize, CancellationToken ct)
    {
        var history = await db.LoyaltyTransactions.AsNoTracking()
            .Where(t => t.CustomerId == customer.Id)
            .OrderByDescending(t => t.CreatedAtUtc)
            .ToPagedAsync(page, pageSize, t => new LoyaltyTransactionResponse(t.Id, t.OrderId, t.Points, t.Type, t.Note, t.CreatedAtUtc), ct);

        var next = thresholds.NextThreshold(customer.Tier);
        return new LoyaltySummaryResponse(customer.PointsBalance, customer.LifetimePoints, customer.Tier, next,
            next is null ? null : Math.Max(0, next.Value - customer.LifetimePoints), history);
    }

    private static async Task<Customer> CurrentAsync(CustomerEngagementDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var userId = user.UserId ?? throw new UnauthorizedException("Not signed in.");
        var tenantId = TenantScope.Require(user);
        return await db.Customers.FirstOrDefaultAsync(c => c.UserId == userId && c.TenantId == tenantId, ct)
            ?? throw new NotFoundException("Customer profile for user", userId); // created asynchronously after registration
    }

    /// <summary>A customer of another brand is reported as not found: its existence is not revealed (BR-04).</summary>
    private static async Task<Customer> FindAsync(CustomerEngagementDbContext db, ICurrentUser user, Guid id, CancellationToken ct)
    {
        var tenantId = TenantScope.Require(user);
        return await db.Customers.FirstOrDefaultAsync(c => c.Id == id && c.TenantId == tenantId, ct) ?? throw new NotFoundException("Customer", id);
    }
}
