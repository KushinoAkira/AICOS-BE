using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;
using AICOS.KitchenOperations.Api.Persistence;
using Microsoft.EntityFrameworkCore;
namespace AICOS.KitchenOperations.Api.Features.Barista;

public static class BaristaEndpoints
{
    public static void AddBaristaWorkflow(this WebApplicationBuilder builder)
    {
        AICOS.BuildingBlocks.Http.ServiceClientExtensions.AddAicosServiceClient<IBarLocationDirectory, BarLocationDirectory>(builder.Services, builder.Configuration, "Table");
        AICOS.BuildingBlocks.Http.ServiceClientExtensions.AddAicosServiceClient<IBarOrderDirectory, BarOrderDirectory>(builder.Services, builder.Configuration, "Order");
        builder.Services.AddScoped<BaristaOperations>();
        builder.Services.AddScoped<BarPrintDispatcher>();
        builder.Services.AddHttpClient<IBarPrinter, VerifiedPrintAgent>(h => h.Timeout = TimeSpan.FromSeconds(10));
        builder.Services.AddHostedService<BarPrintWorker>();
    }
    public static void MapBaristaWorkflow(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/barista").WithTags("Barista workflow").RequireAuthorization(p => p.RequireRole(KitchenEndpoints.BarRoles))
            .AddEndpointFilter(async (ctx, next) => BaristaOperations.Enabled(ctx.HttpContext.RequestServices.GetRequiredService<IConfiguration>()) ? await next(ctx) : Results.StatusCode(503));
        group.MapGet("/tickets", ListAsync);
        group.MapGet("/tickets/{id:guid}", DetailAsync);
        group.MapGet("/summary", SummaryAsync);
        group.MapGet("/table-summary", TableSummaryAsync);
        group.MapPost("/tickets/{id:guid}/accept", async (Guid id, AcceptTicketRequest request, HttpContext http, BaristaOperations operations, KitchenOperationsDbContext db, IEventBus bus, ILoggerFactory logs, CancellationToken ct) =>
        {
            var job = await operations.AcceptAsync(id, request, http.Request.Headers["Idempotency-Key"].ToString(), ct);
            var ticket = await db.Tickets.AsNoTracking().SingleAsync(t => t.Id == id, ct);
            if (ticket.FulfillmentMode == AICOS.Contracts.Serving.FulfillmentModes.WholeOrder)
                try
                {
                    await bus.PublishAsync(new KitchenOrderStartedIntegrationEvent(ticket.TenantId, ticket.Id, ticket.OrderId, ticket.BranchId, ticket.BrewingStartedAtUtc ?? job.CreatedAtUtc), ct);
                }
                catch (Exception) { logs.CreateLogger("Barista").LogWarning("Acceptance committed; operational notification needs refresh."); }
            return Results.Ok(Job(job));
        });
        group.MapPost("/tickets/{id:guid}/labels", (Guid id, CreateBarPrintRequest request, HttpContext http, BaristaOperations operations, CancellationToken ct) => Print(id, "LABELS", request, http, operations, ct));
        group.MapPost("/tickets/{id:guid}/reprint-slip", (Guid id, CreateBarPrintRequest request, HttpContext http, BaristaOperations operations, CancellationToken ct) => Print(id, "SLIP", request, http, operations, ct));
        group.MapGet("/print-jobs/{id:guid}", async (Guid id, KitchenOperationsDbContext db, ICurrentUser user, CancellationToken ct) => Job(await LoadJob(id, db, user, ct)));
        group.MapGet("/print-jobs/{id:guid}/preview", async (Guid id, KitchenOperationsDbContext db, ICurrentUser user, CancellationToken ct) =>
        {
            var job = await LoadJob(id, db, user, ct);
            return Results.Ok(new
            {
                job.Id,
                job.Status,
                job.TestOutput,
                Document = BarPrintDocument.Read(job.Payload),
                Html = BarPrintDocument.Read(job.Payload).Html(job.CreatedAtUtc)
            });
        });
    }
    private static async Task<IResult> Print(Guid id, string kind, CreateBarPrintRequest request, HttpContext http, BaristaOperations ops, CancellationToken ct) => Results.Ok(Job(await ops.PrintAsync(id, kind, request, http.Request.Headers["Idempotency-Key"].ToString(), ct)));
    public static object Job(KitchenPrintJob j) => new { j.Id, j.TicketId, j.Kind, j.Status, j.Cycle, j.RequestedBy, j.ReprintOf, j.CreatedAtUtc, j.StartedAtUtc, j.PrintedAtUtc, j.ErrorCode, j.TestOutput };
    private static async Task<KitchenPrintJob> LoadJob(Guid id, KitchenOperationsDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var tenant = TenantScope.Require(user);
        var row = await db.PrintJobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == id && j.TenantId == tenant, ct) ?? throw new NotFoundException("Print job", id);
        BranchAccess.EnsureCanAccess(user, row.BranchId);
        return row;
    }
    private static async Task<object> DetailAsync(Guid id, KitchenOperationsDbContext db, ICurrentUser user, IBarLocationDirectory locations, IBarOrderDirectory orders, CancellationToken ct)
    {
        var ticket = await TicketAccess.LoadAsync(db, user, id, ct, track: false);
        var acceptance = await db.BaristaAcceptances.AsNoTracking().SingleOrDefaultAsync(a => a.TicketId == id && a.Cycle == ticket.RemakeCount, ct);
        var jobs = await db.PrintJobs.AsNoTracking().Where(j => j.TicketId == id).OrderByDescending(j => j.CreatedAtUtc).Take(50).ToListAsync(ct);
        var cups = await db.CupTasks.AsNoTracking().Where(c => c.TicketId == id).OrderBy(c => c.OrderCupIndex).ToListAsync(ct);
        return new
        {
            Ticket = TicketMapper.ToDto(ticket),
            AreaName = await locations.AreaAsync(ticket.BranchId, ticket.TableNumber, ct),
            OrderedAtUtc = await orders.CreatedAtAsync(ticket.OrderId, ticket.BranchId, ct),
            SentToBarAtUtc = ticket.CreatedAtUtc,
            ticket.FulfillmentMode,
            ticket.RecoveryHoldReason,
            Acceptance = acceptance,
            Items = ticket.Items.Select(i => new { i.Id, i.ProductId, i.ProductName, i.Quantity, i.Note }),
            Cups = cups,
            PrintJobs = jobs.Select(Job),
            ServerTimeUtc = DateTime.UtcNow
        };
    }
    private static async Task<object> ListAsync(Guid? branchId, string? status, bool? history, string? tableNumber, int? offset, int? limit, KitchenOperationsDbContext db, ICurrentUser user, IBranchDirectory branches, CancellationToken ct)
    {
        var tenant = TenantScope.Require(user);
        var branch = await BranchScope.ResolveAsync(user, branches, branchId, false, ct);
        var skip = Math.Clamp(offset ?? 0, 0, 10000);
        var take = Math.Clamp(limit ?? 30, 1, 100);
        var query = db.Tickets.AsNoTracking().Include(t => t.Items).Include(t => t.Stickers).AsSplitQuery().Where(t => t.TenantId == tenant && t.BranchId == branch);
        query = history == true ? query.Where(t => t.ReadyAtUtc != null || t.Status == Domain.Enums.KitchenTicketStatus.Completed || t.Status == Domain.Enums.KitchenTicketStatus.Cancelled) : query.Where(t => t.Status != Domain.Enums.KitchenTicketStatus.Completed && t.Status != Domain.Enums.KitchenTicketStatus.Cancelled && (t.IsPaid || (t.OrderType == "DineIn" && !t.PayFirst)));
        if (tableNumber is not null)
            query = query.Where(t => t.TableNumber == tableNumber);
        if (status is not null && status != "All")
        {
            if (status == "Waiting")
                query = query.Where(t => !db.BaristaAcceptances.Any(a => a.TicketId == t.Id && a.Cycle == t.RemakeCount));
            else if (Enum.TryParse<Domain.Enums.KitchenTicketStatus>(status, out var parsed))
                query = query.Where(t => t.Status == parsed);
            else
                throw new ValidationFailedException("status", "Unknown preparation filter.");
        }
        var rows = await query.OrderByDescending(t => t.CreatedAtUtc).ThenBy(t => t.Id).Skip(skip).Take(take + 1).ToListAsync(ct);
        var more = rows.Count > take;
        rows = rows.Take(take).ToList();
        var ids = rows.Select(t => t.Id).ToArray();
        var accept = await db.BaristaAcceptances.AsNoTracking().Where(a => ids.Contains(a.TicketId)).ToListAsync(ct);
        return new
        {
            Items = rows.Select(t => new { Ticket = TicketMapper.ToDto(t), t.FulfillmentMode, t.RecoveryHoldReason, Acceptance = accept.SingleOrDefault(a => a.TicketId == t.Id && a.Cycle == t.RemakeCount) }),
            Offset = skip,
            HasMore = more
        };
    }
    private static async Task<object> TableSummaryAsync(Guid? branchId, KitchenOperationsDbContext db, ICurrentUser user, IBranchDirectory branches, CancellationToken ct)
    {
        var tenant = TenantScope.Require(user);
        var branch = await BranchScope.ResolveAsync(user, branches, branchId, false, ct);
        return await db.Tickets.AsNoTracking().Where(t => t.TenantId == tenant && t.BranchId == branch && t.Status != Domain.Enums.KitchenTicketStatus.Completed && t.Status != Domain.Enums.KitchenTicketStatus.Cancelled && (t.IsPaid || (t.OrderType == "DineIn" && !t.PayFirst)))
            .GroupBy(t => new { t.TableNumber, t.Status }).Select(g => new { g.Key.TableNumber, Status = g.Key.Status.ToString(), Orders = g.Count(), Cups = g.Sum(t => t.Items.Sum(i => i.Quantity)) }).ToListAsync(ct);
    }
    private static async Task<object> SummaryAsync(Guid? branchId, KitchenOperationsDbContext db, ICurrentUser user, IBranchDirectory branches, TimeProvider clock, CancellationToken ct)
    {
        var tenant = TenantScope.Require(user);
        var branch = await BranchScope.ResolveAsync(user, branches, branchId, false, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        // Vietnam's current day begins at 17:00 UTC of the preceding calendar day.
        var start = now.AddHours(7).Date.AddHours(-7);
        var end = start.AddDays(1);
        var all = db.Tickets.Where(t => t.TenantId == tenant && t.BranchId == branch && (t.IsPaid || (t.OrderType == "DineIn" && !t.PayFirst)));
        var completed = db.BaristaAcceptances.Where(a => a.TenantId == tenant && a.BranchId == branch && a.CompletedAtUtc >= start && a.CompletedAtUtc < end);
        var seconds = await completed.Select(a => (double?)(a.CompletedAtUtc!.Value - a.AcceptedAtUtc).TotalSeconds).AverageAsync(ct);
        var whole = await completed.Join(db.Tickets.Where(t => t.FulfillmentMode == "WholeOrder"), a => a.TicketId, t => t.Id, (a, t) => t).SelectMany(t => t.Items).SumAsync(i => (int?)i.Quantity, ct) ?? 0;
        var cups = await db.CupTasks.CountAsync(c => c.TenantId == tenant && c.BranchId == branch && c.ReadyAtUtc >= start && c.ReadyAtUtc < end, ct);
        return new
        {
            Waiting = await all.CountAsync(t => t.Status == Domain.Enums.KitchenTicketStatus.Pending && !db.BaristaAcceptances.Any(a => a.TicketId == t.Id && a.Cycle == t.RemakeCount), ct),
            Brewing = await all.CountAsync(t => t.Status == Domain.Enums.KitchenTicketStatus.Brewing, ct),
            CompletedOrders = await completed.CountAsync(ct),
            CompletedCups = whole + cups,
            AverageSeconds = seconds,
            FromUtc = start,
            ToUtc = end,
            ServerTimeUtc = now
        };
    }
}
