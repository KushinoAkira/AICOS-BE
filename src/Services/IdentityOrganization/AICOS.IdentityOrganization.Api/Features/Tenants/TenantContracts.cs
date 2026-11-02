using System.Text.RegularExpressions;
using AICOS.BuildingBlocks.Security;
using AICOS.BuildingBlocks.Validation;
using AICOS.IdentityOrganization.Api.Domain;
using AICOS.IdentityOrganization.Api.Features.Common;
using AICOS.IdentityOrganization.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.IdentityOrganization.Api.Features.Tenants;

/// <summary>First Brand Admin created together with the tenant (FR-02).</summary>
public sealed record FirstAdminRequest(string? FullName, string? Email, string? Phone, string? Password);

/// <summary>POST /tenants. <see cref="Code"/> is the tenant identifier / subdomain.</summary>
/// <summary><see cref="CreateTenantRequest.PlanId"/> is optional: when given, the tenant starts on that plan (UC-05 + UC-07 in one step).</summary>
public sealed record CreateTenantRequest(string? Name, string? Code, FirstAdminRequest? FirstAdmin, Guid? PlanId = null);

public sealed record UpdateTenantRequest(string? Name);

public sealed record ChangeTenantStatusRequest(TenantStatus? Status);

/// <summary><see cref="PlanId"/> / <see cref="PlanName"/> are the plan of the current subscription (null when the tenant has none); <see cref="BranchCount"/> counts its branches.</summary>
public sealed record TenantResponse(
    Guid Id, string Name, string Code, TenantStatus Status, DateTime CreatedAtUtc, DateTime? UpdatedAtUtc,
    Guid? PlanId = null, string? PlanName = null, int BranchCount = 0, string? OwnerName = null, string? OwnerEmail = null, string? OwnerPhone = null,
    string? ReviewNote = null, string? LogoUrl = null)
{
    public static TenantResponse From(Tenant t) => new(t.Id, t.Name, t.Code, t.Status, t.CreatedAtUtc, t.UpdatedAtUtc, ReviewNote: t.ReviewNote, LogoUrl: t.LogoUrl);

    /// <summary>Adds the plan and the branch count with two grouped queries for the whole list, not one per tenant.</summary>
    public static async Task<List<TenantResponse>> FromManyAsync(IReadOnlyCollection<Tenant> tenants, IdentityOrganizationDbContext db, CancellationToken ct)
    {
        var ids = tenants.Select(t => t.Id).ToList();
        var plans = await db.TenantSubscriptions.AsNoTracking()
            .Where(s => ids.Contains(s.TenantId) && s.Status == SubscriptionStatus.Active)
            .Select(s => new { s.TenantId, s.PlanId, s.PlanName })
            .ToListAsync(ct);
        var branches = await db.Branches.AsNoTracking()
            .Where(b => ids.Contains(b.TenantId))
            .GroupBy(b => b.TenantId)
            .Select(g => new { TenantId = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var owners = await db.Users.AsNoTracking()
            .Where(u => u.TenantId != null && ids.Contains(u.TenantId.Value) && u.Role == AicosRoles.BrandAdmin)
            .OrderBy(u => u.CreatedAtUtc)
            .Select(u => new { u.TenantId, u.FullName, u.Email, u.Phone })
            .ToListAsync(ct);
        return tenants.Select(t =>
        {
            var plan = plans.FirstOrDefault(p => p.TenantId == t.Id);
            var owner = owners.FirstOrDefault(o => o.TenantId == t.Id);
            return From(t) with
            {
                PlanId = plan?.PlanId,
                PlanName = plan?.PlanName,
                BranchCount = branches.FirstOrDefault(b => b.TenantId == t.Id)?.Count ?? 0,
                OwnerName = owner?.FullName,
                OwnerEmail = owner?.Email,
                OwnerPhone = owner?.Phone,
            };
        }).ToList();
    }

    public static async Task<TenantResponse> FromAsync(Tenant tenant, IdentityOrganizationDbContext db, CancellationToken ct) =>
        (await FromManyAsync([tenant], db, ct))[0];
}

internal static partial class TenantRequestValidator
{
    public static void EnsureValid(CreateTenantRequest r)
    {
        var errors = new ValidationErrors();
        CommonRules.Required(errors, nameof(r.Name), r.Name, "Tenant name", Tenant.NameMaxLength);

        if (string.IsNullOrWhiteSpace(r.Code) || !CodeRegex().IsMatch(r.Code.Trim().ToLowerInvariant()))
        {
            errors.Add(nameof(r.Code), "Code must be 3-50 characters of letters, digits or '-', starting with a letter.");
        }

        if (r.FirstAdmin is null)
        {
            errors.Add(nameof(r.FirstAdmin), "The first Brand Admin account is required.");
        }
        else
        {
            CommonRules.Required(errors, "FirstAdmin.FullName", r.FirstAdmin.FullName, "Full name", User.FullNameMaxLength);
            CommonRules.Email(errors, "FirstAdmin.Email", r.FirstAdmin.Email);
            CommonRules.Phone(errors, "FirstAdmin.Phone", r.FirstAdmin.Phone);
            InputRules.NewPassword(errors, "FirstAdmin.Password", r.FirstAdmin.Password, "FirstAdmin.Password", r.FirstAdmin.Password);
        }

        errors.ThrowIfAny();
    }

    public static void EnsureValid(UpdateTenantRequest r)
    {
        var errors = new ValidationErrors();
        CommonRules.Required(errors, nameof(r.Name), r.Name, "Tenant name", Tenant.NameMaxLength);
        errors.ThrowIfAny();
    }

    [GeneratedRegex("^[a-z][a-z0-9-]{2,49}$")]
    private static partial Regex CodeRegex();
}
