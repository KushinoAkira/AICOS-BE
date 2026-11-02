namespace AICOS.BuildingBlocks.Security;

/// <summary>The 7 actors of the proposal (BR-01): exactly one role per account.</summary>
public static class AicosRoles
{
    /// <summary>Platform owner. Manages tenants, plans and platform config; never sees a brand's business data (BR-06).</summary>
    public const string SuperAdmin = "SuperAdmin";

    /// <summary>Owner/admin of one brand (tenant). Works chain-wide inside the tenant.</summary>
    public const string BrandAdmin = "BrandAdmin";

    public const string BranchManager = "BranchManager";
    public const string Barista = "Barista";
    public const string Cashier = "Cashier";
    public const string Customer = "Customer";
    public const string Guest = "Guest";

    public static readonly IReadOnlyList<string> All =
        [SuperAdmin, BrandAdmin, BranchManager, Barista, Cashier, Customer, Guest];
}

/// <summary>Authorization policy names. Use with <c>RequireAuthorization(AicosPolicies.X)</c>.</summary>
public static class AicosPolicies
{
    /// <summary>Platform level: SuperAdmin only (tenants, subscription plans).</summary>
    public const string PlatformAdmin = nameof(PlatformAdmin);

    /// <summary>Brand level: BrandAdmin only (branches, accounts, menu, policies).</summary>
    public const string AdminOnly = nameof(AdminOnly);

    /// <summary>BrandAdmin or BranchManager.</summary>
    public const string Management = nameof(Management);

    /// <summary>Who may read the audit history: SuperAdmin (platform level) and BrandAdmin (own brand). See UC-11.</summary>
    public const string AuditViewer = nameof(AuditViewer);

    /// <summary>Who may mark a product sold out or available again at a branch: BrandAdmin, BranchManager, Barista (UC-24; Barista is an assumption).</summary>
    public const string AvailabilityEditors = nameof(AvailabilityEditors);

    /// <summary>Anyone who works for a brand: BrandAdmin, BranchManager, Barista, Cashier.</summary>
    public const string Staff = nameof(Staff);
}

/// <summary>Claim names used in our JWTs (raw JWT names, no WS-* claim mapping).</summary>
public static class AicosClaimTypes
{
    public const string UserId = "sub";
    public const string Name = "name";
    public const string Role = "role";
    public const string TenantId = "tenant_id";
    public const string BranchId = "branch_id";

    /// <summary>The sign-in the token came from, so a person can tell this device from their others.</summary>
    public const string SessionId = "sid";
}
