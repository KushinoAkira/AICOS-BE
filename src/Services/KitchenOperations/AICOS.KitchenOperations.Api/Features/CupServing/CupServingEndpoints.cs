using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.KitchenAdmission;
using AICOS.KitchenOperations.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.KitchenOperations.Api.Features.CupServing;

public sealed record CupActionRequest(long ExpectedVersion, int ExpectedCycle, Guid? ClaimId = null,
    long? Fence = null, bool ConfirmedAtBar = false);

public static class CupServingEndpoints
{
    public static void AddCupServing(this WebApplicationBuilder builder)
    {
        builder.Services.AddScoped<CupAdmissionWriter>();
        builder.Services.AddScoped<FulfillmentDispatcher>();
        builder.Services.AddHttpClient<IFulfillmentTransport, HttpFulfillmentTransport>(client =>
        {
            client.BaseAddress = new Uri(builder.Configuration["Services:Order:BaseUrl"] ?? "http://order:8080");
            client.Timeout = TimeSpan.FromSeconds(8);
        });
        if (builder.Configuration.GetValue<bool>("CupServing:DispatcherEnabled"))
            FulfillmentMachineAuthentication.Key(builder.Configuration);
        builder.Services.AddHostedService<FulfillmentWorker>();
        builder.Services.AddScoped(sp => new CupServingUnitOfWork(sp.GetRequiredService<KitchenOperationsDbContext>(),
            sp.GetRequiredService<ICurrentUser>(), sp.GetRequiredService<TimeProvider>(),
            LeaseApproved(builder.Configuration) ? TimeSpan.FromSeconds(builder.Configuration.GetValue<int>("CupServing:LeaseSeconds")) : TimeSpan.Zero));
        if (builder.Configuration.GetValue<bool>("CupServing:AdmissionEnabled"))
            builder.Services.AddAdmissionMachineAuthentication(builder.Configuration);
    }

    public static bool LeaseApproved(IConfiguration config) => config.GetValue<bool>("CupServing:LeasePolicyApproved")
        && config.GetValue<int>("CupServing:LeaseSeconds") > 0;

    public static void MapCupServing(this WebApplication app)
    {
        if (app.Configuration.GetValue<bool>("CupServing:AdmissionEnabled"))
            app.MapPost("/internal/v2/kitchen/order-admissions", async (AICOS.Contracts.Serving.CupAdmissionRequest request,
                HttpContext http, CupAdmissionWriter writer, CancellationToken ct) =>
            {
                if (http.User.FindFirst("tenant_id")?.Value != request.TenantId.ToString()
                    || http.User.FindFirst("branch_id")?.Value != request.BranchId.ToString()) throw new ForbiddenException("Machine scope differs.");
                var ack = await writer.ApplyAsync(request, ct);
                return Results.Json(ack, statusCode: ack.ReceiptStatus == "Applied" ? 200 : 202);
            }).ExcludeFromDescription().RequireAuthorization(p => p.AddAuthenticationSchemes(AdmissionMachineAuthentication.Scheme)
                .RequireAuthenticatedUser().RequireClaim("scope", "kitchen.admission.v2.write"));
        if (!app.Configuration.GetValue<bool>("CupServing:ApiEnabled")) return;
        var group = app.MapGroup("/api/v1/kitchen/cups").WithTags("Cup Serving").RequireAuthorization(AicosPolicies.Staff);
        group.MapGet("", async (Guid orderId, KitchenOperationsDbContext db, ICurrentUser user, CancellationToken ct) =>
        {
            var tenant = TenantScope.Require(user);
            var ticket = await db.Tickets.AsNoTracking().Include(t => t.Items).Include(t => t.Stickers).AsSplitQuery()
                .SingleOrDefaultAsync(t => t.OrderId == orderId && t.TenantId == tenant, ct)
                ?? throw new NotFoundException("Order ticket", orderId);
            BranchAccess.EnsureCanAccess(user, ticket.BranchId);
            var cups = await db.CupTasks.AsNoTracking().Where(c => c.TicketId == ticket.Id).OrderBy(c => c.OrderCupIndex).ToListAsync(ct);
            var ids = cups.Select(c => c.Id).ToArray();
            var claims = await db.CupClaims.AsNoTracking().Where(c => ids.Contains(c.CupId)
                && (c.State == CupClaimState.Reserved || c.State == CupClaimState.Carrying || c.State == CupClaimState.Overdue)).ToListAsync(ct);
            return new
            {
                ticket.Id,
                ticket.OrderId,
                ticket.RecoveryHoldReason,
                ServerTimeUtc = DateTime.UtcNow,
                EligibleForBar = ticket.IsEligibleForBar && ticket.Status is not (Domain.Enums.KitchenTicketStatus.Completed or Domain.Enums.KitchenTicketStatus.Cancelled),
                LeasePolicyApproved = LeaseApproved(app.Configuration),
                Cups = cups.Select(c => new
                {
                    Cup = c,
                    ProductName = ticket.Items.Single(i => i.Id == c.TicketItemId).ProductName,
                    StickerId = ticket.Stickers.Single(s => s.CupTaskId == c.Id).Id,
                    PrintStatus = "UNKNOWN",
                    Claim = claims.SingleOrDefault(x => x.CupId == c.Id)
                })
            };
        });
        foreach (var action in Enum.GetValues<CupAction>())
        {
            var captured = action;
            group.MapPost("/{cupId:guid}/" + action.ToString().ToLowerInvariant(), async (Guid cupId,
                CupActionRequest request, HttpContext http, CupServingUnitOfWork operations, CancellationToken ct) =>
            {
                if (captured is CupAction.Claim or CupAction.Renew && !LeaseApproved(app.Configuration))
                    return Results.Problem(statusCode: 503, title: "Claim lease policy awaits approval.");
                var key = http.Request.Headers["Idempotency-Key"].ToString();
                var result = await operations.ExecuteAsync(new(cupId, key, captured, request.ExpectedVersion,
                    request.ExpectedCycle, request.ClaimId, request.Fence, request.ConfirmedAtBar), ct);
                return Results.Ok(result);
            });
        }
    }
}
