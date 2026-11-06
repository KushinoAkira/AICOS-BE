using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.KitchenAdmission;
using AICOS.KitchenOperations.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.KitchenOperations.Api.Features.KitchenAdmission;

public static class KitchenAdmissionEndpoints
{
    public static void MapKitchenAdmission(this WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>("KitchenAdmission:ReceiverEnabled")) return;
        var group = app.MapGroup("/internal/v1/kitchen/order-admissions").ExcludeFromDescription();
        group.MapPost("", async (AdmissionRequest request, HttpContext http, KitchenAdmissionService service, CancellationToken ct) =>
        {
            RequireScope(http, request.TenantId, request.BranchId);
            var ack = await service.EnsureAsync(request, true, ct);
            return Results.Json(ack, statusCode: ack.ReceiptStatus == "Applied" ? 200 : 202);
        }).RequireAuthorization(p => p.AddAuthenticationSchemes(AdmissionMachineAuthentication.Scheme).RequireAuthenticatedUser().RequireClaim("scope", "kitchen.admission.write"));
        group.MapPost("/lookup", async (AdmissionLookup request, HttpContext http, KitchenOperationsDbContext db, CancellationToken ct) =>
        {
            RequireScope(http, request.TenantId, request.BranchId);
            if (request.OrderIds is null || request.OrderIds.Count is < 1 or > 100 || request.OrderIds.Contains(Guid.Empty))
                throw new ValidationFailedException("orderIds", "Supply 1 to 100 order IDs.");
            var tickets = await db.Tickets.AsNoTracking().Where(t => t.TenantId == request.TenantId && t.BranchId == request.BranchId
                && request.OrderIds.Contains(t.OrderId)).ToListAsync(ct);
            return request.OrderIds.Distinct().Select(id =>
            {
                var ticket = tickets.SingleOrDefault(t => t.OrderId == id);
                return new AdmissionPresence(id, ticket?.Id, ticket?.Status.ToString(), ticket?.RecoveryHoldReason, ticket?.TableNumber, ticket?.CompletedAtUtc);
            }).ToArray();
        }).RequireAuthorization(p => p.AddAuthenticationSchemes(AdmissionMachineAuthentication.Scheme).RequireAuthenticatedUser().RequireClaim("scope", "kitchen.admission.lookup"));
        app.MapGet("/api/v1/kitchen/recovery/status", async (KitchenOperationsDbContext db, ICurrentUser user, CancellationToken ct) =>
        {
            var tenant = TenantScope.Require(user);
            return await db.Tickets.AsNoTracking().Where(t => t.TenantId == tenant && t.RecoveryHoldReason != null
                && (user.IsInRole(AicosRoles.BrandAdmin) || t.BranchId == user.BranchId))
                .Select(t => new { t.Id, t.OrderId, t.OrderCode, t.BranchId, t.RecoveryHoldReason }).Take(100).ToListAsync(ct);
        }).RequireAuthorization(AicosPolicies.Management);
    }

    private static void RequireScope(HttpContext http, Guid tenant, Guid branch)
    {
        if (http.User.FindFirst("tenant_id")?.Value != tenant.ToString() || http.User.FindFirst("branch_id")?.Value != branch.ToString())
            throw new ForbiddenException("Machine scope does not match admission scope.");
    }
}
