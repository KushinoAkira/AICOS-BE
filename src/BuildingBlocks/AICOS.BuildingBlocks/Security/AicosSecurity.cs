namespace AICOS.BuildingBlocks.Security;

/// <summary>The 7 system roles from the proposal (FE-01).</summary>
public static class AicosRoles
{
    public const string SystemAdmin = "SystemAdmin";
    public const string Manager = "Manager";
    public const string Cashier = "Cashier";
    public const string WarehouseStaff = "WarehouseStaff";
    public const string AiBusinessAssistant = "AiBusinessAssistant";
    public const string Customer = "Customer";
    public const string Guest = "Guest";

    public static readonly IReadOnlyList<string> All =
        [SystemAdmin, Manager, Cashier, WarehouseStaff, AiBusinessAssistant, Customer, Guest];
}

/// <summary>Authorization policy names. Use with <c>RequireAuthorization(AicosPolicies.X)</c>.</summary>
public static class AicosPolicies
{
    public const string AdminOnly = nameof(AdminOnly);

    /// <summary>SystemAdmin or Manager.</summary>
    public const string Management = nameof(Management);

    /// <summary>Anyone who works in a branch: SystemAdmin, Manager, Cashier, WarehouseStaff.</summary>
    public const string Staff = nameof(Staff);

    /// <summary>Registered customer (mobile app).</summary>
    public const string Customer = nameof(Customer);
}

/// <summary>Claim names used in our JWTs (raw JWT names, no WS-* claim mapping).</summary>
public static class AicosClaimTypes
{
    public const string UserId = "sub";
    public const string Name = "name";
    public const string Role = "role";
    public const string BranchId = "branch_id";
    public const string TenantId = "tenant_id";
}
