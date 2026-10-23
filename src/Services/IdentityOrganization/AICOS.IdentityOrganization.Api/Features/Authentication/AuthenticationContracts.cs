namespace AICOS.IdentityOrganization.Api.Features.Authentication;

public sealed record LoginRequest(string Email, string Password, Guid? BranchId);

public sealed record BranchOptionResponse(Guid Id, string Code, string Name);

public sealed record StaffProfileResponse(
    Guid UserId,
    string FullName,
    string Email,
    string Role,
    Guid TenantId,
    Guid BranchId,
    string BranchCode,
    string BranchName);

public sealed record LoginResponse(
    bool RequiresBranchSelection,
    string? AccessToken,
    DateTime? ExpiresAtUtc,
    StaffProfileResponse? Staff,
    IReadOnlyList<BranchOptionResponse> Branches);
