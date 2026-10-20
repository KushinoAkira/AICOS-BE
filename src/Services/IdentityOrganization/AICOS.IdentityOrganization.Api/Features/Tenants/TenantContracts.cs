using System.Text.RegularExpressions;
using AICOS.BuildingBlocks.Validation;
using AICOS.IdentityOrganization.Api.Domain;
using AICOS.IdentityOrganization.Api.Features.Common;

namespace AICOS.IdentityOrganization.Api.Features.Tenants;

/// <summary>First Brand Admin created together with the tenant (FR-02).</summary>
public sealed record FirstAdminRequest(string? FullName, string? Email, string? Phone, string? Password);

/// <summary>POST /tenants. <see cref="Code"/> is the tenant identifier / subdomain.</summary>
/// <summary><see cref="CreateTenantRequest.PlanId"/> is optional: when given, the tenant starts on that plan (UC-05 + UC-07 in one step).</summary>
public sealed record CreateTenantRequest(string? Name, string? Code, FirstAdminRequest? FirstAdmin, Guid? PlanId = null);

public sealed record UpdateTenantRequest(string? Name);

public sealed record ChangeTenantStatusRequest(TenantStatus? Status);

public sealed record TenantResponse(Guid Id, string Name, string Code, TenantStatus Status, DateTime CreatedAtUtc, DateTime? UpdatedAtUtc)
{
    public static TenantResponse From(Tenant t) => new(t.Id, t.Name, t.Code, t.Status, t.CreatedAtUtc, t.UpdatedAtUtc);
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
