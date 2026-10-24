namespace AICOS.IdentityOrganization.Api.Features.StaffProvisioning;

public sealed record CreateStaffRequest(
    string Email,
    string Password,
    string FullName,
    Guid BranchId,
    string Role);

public sealed record StaffAccountResponse(
    Guid UserId,
    string Email,
    string FullName,
    Guid TenantId,
    Guid BranchId,
    string BranchCode,
    string BranchName,
    string Role,
    string Status);
