using AICOS.BuildingBlocks.Security;

namespace AICOS.IdentityOrganization.Api.Features.StaffProvisioning;

public static class StaffProvisioningEndpoints
{
    public static RouteGroupBuilder MapStaffProvisioningEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/staff", async (
            CreateStaffRequest request,
            ICurrentUser currentUser,
            StaffProvisioningService provisioning,
            CancellationToken cancellationToken) =>
            {
                var staff = await provisioning.CreateAsync(request, currentUser, cancellationToken);
                return Results.Created($"/api/v1/identity/staff/{staff.UserId}", staff);
            })
            .RequireAuthorization(AicosPolicies.Management)
            .Produces<StaffAccountResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return api;
    }
}
