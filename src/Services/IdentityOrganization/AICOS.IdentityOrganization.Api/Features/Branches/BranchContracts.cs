using AICOS.BuildingBlocks.Validation;
using AICOS.IdentityOrganization.Api.Domain;

namespace AICOS.IdentityOrganization.Api.Features.Branches;

/// <summary>Body of POST /branches and PUT /branches/{id}. Times use "HH:mm" (e.g. "07:00").</summary>
public sealed record BranchRequest(string? Name, string? Address, string? Phone, TimeOnly? OpeningTime, TimeOnly? ClosingTime);

public sealed record ChangeBranchStatusRequest(BranchStatus? Status);

public sealed record BranchResponse(
    Guid Id,
    string Name,
    string? Address,
    string? Phone,
    TimeOnly? OpeningTime,
    TimeOnly? ClosingTime,
    BranchStatus Status,
    DateTime CreatedAtUtc,
    DateTime? UpdatedAtUtc)
{
    public static BranchResponse From(Branch branch) => new(
        branch.Id,
        branch.Name,
        branch.Address,
        branch.Phone,
        branch.OpeningTime,
        branch.ClosingTime,
        branch.Status,
        branch.CreatedAtUtc,
        branch.UpdatedAtUtc);
}

internal static class BranchRequestValidator
{
    public static void EnsureValid(BranchRequest request)
    {
        var errors = new ValidationErrors();

        CommonRules.Required(errors, nameof(request.Name), request.Name, "Branch name", Branch.NameMaxLength);

        if (request.Address?.Trim().Length > Branch.AddressMaxLength)
        {
            errors.Add(nameof(request.Address), $"Address must be at most {Branch.AddressMaxLength} characters.");
        }

        CommonRules.Phone(errors, nameof(request.Phone), request.Phone);

        if (request.OpeningTime is null != request.ClosingTime is null)
        {
            errors.Add(nameof(request.OpeningTime), "Opening time and closing time must be provided together.");
        }
        else if (request.OpeningTime == request.ClosingTime && request.OpeningTime is not null)
        {
            errors.Add(nameof(request.ClosingTime), "Closing time must differ from opening time.");
        }

        errors.ThrowIfAny();
    }
}
