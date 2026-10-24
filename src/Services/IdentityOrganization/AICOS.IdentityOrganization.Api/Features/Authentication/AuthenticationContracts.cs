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
    string? RefreshToken,
    DateTime? RefreshTokenExpiresAtUtc,
    StaffProfileResponse? Staff,
    IReadOnlyList<BranchOptionResponse> Branches);

public sealed record RefreshRequest(string RefreshToken);

public sealed record RefreshResponse(
    string AccessToken,
    DateTime ExpiresAtUtc,
    string RefreshToken,
    DateTime RefreshTokenExpiresAtUtc,
    StaffProfileResponse Staff);

public sealed record LogoutRequest(string RefreshToken);
