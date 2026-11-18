using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Serving;
using AICOS.Ordering.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.Ordering.Api.Features.Fulfillment;

public static class FulfillmentEndpoints
{
    public static void AddFulfillment(this WebApplicationBuilder builder)
    {
        builder.Services.AddScoped<FulfillmentProjectionWriter>();
        if (builder.Configuration.GetValue<bool>("CupServing:ProjectionReceiverEnabled"))
            FulfillmentMachineAuthentication.Register(builder.Services, builder.Configuration);
    }

    public static void MapFulfillment(this WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>("CupServing:ProjectionReceiverEnabled")) return;
        app.MapPost("/internal/v1/ordering/cup-fulfillment", async (FulfillmentSnapshot snapshot,
            HttpContext http, FulfillmentProjectionWriter writer, CancellationToken ct) =>
        {
            if (http.User.FindFirst("tenant_id")?.Value != snapshot.TenantId.ToString()
                || http.User.FindFirst("branch_id")?.Value != snapshot.BranchId.ToString())
                throw new ForbiddenException("Machine scope differs.");
            return await writer.ApplyAsync(snapshot, ct);
        }).ExcludeFromDescription().RequireAuthorization(p => p.AddAuthenticationSchemes(FulfillmentMachineAuthentication.Scheme)
            .RequireAuthenticatedUser().RequireClaim("scope", "ordering.fulfillment.write"));
        app.MapGet("/api/v1/orders/{orderId:guid}/fulfillment", async (Guid orderId, OrderingDbContext db,
            ICurrentUser user, CancellationToken ct) =>
        {
            var tenant = TenantScope.Require(user);
            var row = await db.FulfillmentProjection.AsNoTracking().SingleOrDefaultAsync(p => p.OrderId == orderId && p.TenantId == tenant, ct)
                ?? throw new NotFoundException("Fulfillment projection", orderId);
            BranchAccess.EnsureCanAccess(user, row.BranchId); return row;
        }).RequireAuthorization(AicosPolicies.Staff);
    }
}
