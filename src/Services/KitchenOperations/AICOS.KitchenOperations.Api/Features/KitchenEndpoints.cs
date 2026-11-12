using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;
using AICOS.KitchenOperations.Api.Domain.Entities;
using AICOS.KitchenOperations.Api.Domain.Enums;
using AICOS.KitchenOperations.Api.Persistence;
using AICOS.KitchenOperations.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace AICOS.KitchenOperations.Api.Features;

public sealed record UpdateKitchenTicketStatusRequest(KitchenTicketStatus? Status, KitchenTicketStatus? ExpectedStatus = null, int? ExpectedRemakeCount = null);

/// <summary>POST /tickets/{id}/remake: why the order is made again (spilled, wrong drink, quality).</summary>
public sealed record RemakeTicketRequest(string? Reason);

/// <summary>
/// The bar queue (FR-33, FR-35, FR-36). Routes: /api/v1/kitchen. Tickets are created from the order event, never by hand. Barista, Branch
/// Manager and Brand Admin run the bar; the Cashier can read the queue. Everything is scoped to the brand and the branch of the caller.
/// </summary>
public static class KitchenEndpoints
{
    public static readonly string[] BarRoles = [AicosRoles.Barista, AicosRoles.BranchManager, AicosRoles.BrandAdmin];

    public static void MapKitchenOperationsEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup(string.Empty).WithTags("Kitchen Operations");

        group.MapGet("/staff/capabilities", (IConfiguration config) => new
        {
            ConditionalTicketUpdatesVersion = 1,
            CupServingVersion = config.GetValue<bool>("CupServing:ApiEnabled") ? 1 : 0,
            ClaimVersion = config.GetValue<bool>("CupServing:ApiEnabled") && CupServing.CupServingEndpoints.LeaseApproved(config) ? 1 : 0
        })
            .RequireAuthorization(AicosPolicies.Staff);
        group.MapGet("/queue", QueueAsync).RequireAuthorization(AicosPolicies.Staff);
        group.MapGet("/batches", BatchesAsync).RequireAuthorization(AicosPolicies.Staff);
        group.MapGet("/tickets/{id:guid}", GetAsync).RequireAuthorization(AicosPolicies.Staff);
        group.MapPut("/tickets/{id:guid}/status", UpdateStatusAsync).RequireAuthorization(AicosPolicies.Staff);
        group.MapGet("/tickets/{id:guid}/stickers", StickersAsync).RequireAuthorization(b => b.RequireRole(BarRoles));
        group.MapKitchenStickerPreview();
        group.MapPost("/tickets/{id:guid}/remake", RemakeAsync).RequireAuthorization(b => b.RequireRole(BarRoles));
    }

    /// <summary>Priority tickets (scheduled pickup) first by pickup time, then everything else first-in first-out.</summary>
    private static async Task<List<KitchenTicketDto>> QueueAsync(
        KitchenOperationsDbContext db, ICurrentUser user, IBranchDirectory directory, Guid? branchId, CancellationToken ct)
    {
        var tenantId = TenantScope.Require(user);
        var branch = await BranchScope.ResolveAsync(user, directory, branchId, requireActive: false, ct);
        var active = await db.Tickets.AsNoTracking().Include(t => t.Items).Include(t => t.Stickers).AsSplitQuery()
            .Where(t => t.TenantId == tenantId && t.BranchId == branch
                && t.Status != KitchenTicketStatus.Completed && t.Status != KitchenTicketStatus.Cancelled
                && t.RecoveryHoldReason == null && t.FulfillmentMode == AICOS.Contracts.Serving.FulfillmentModes.WholeOrder
                && (t.IsPaid || t.OrderType == "DineIn"))
            .ToListAsync(ct);

        return active.Where(t => t.IsPriority).OrderBy(t => t.ScheduledPickupTimeUtc)
            .Concat(active.Where(t => !t.IsPriority).OrderBy(t => t.CreatedAtUtc))
            .Select(TicketMapper.ToDto).ToList();
    }

    /// <summary>Identical drinks waiting or brewing are grouped into batches (FR-34), at most four cups to a batch.</summary>
    private static async Task<List<BrewingBatchDto>> BatchesAsync(
        KitchenOperationsDbContext db, ICurrentUser user, IBranchDirectory directory, TimeProvider clock, Guid? branchId, CancellationToken ct)
    {
        const int maxBatchSize = 4;
        var tenantId = TenantScope.Require(user);
        var branch = await BranchScope.ResolveAsync(user, directory, branchId, requireActive: false, ct);
        var threshold = clock.GetUtcNow().UtcDateTime - TimeSpan.FromMinutes(5);

        var tickets = await db.Tickets.AsNoTracking().Include(t => t.Items).AsSplitQuery()
            .Where(t => t.TenantId == tenantId && t.BranchId == branch
                && (t.Status == KitchenTicketStatus.Pending || t.Status == KitchenTicketStatus.Brewing)
                && t.RecoveryHoldReason == null && t.FulfillmentMode == AICOS.Contracts.Serving.FulfillmentModes.WholeOrder
                && (t.IsPaid || t.OrderType == "DineIn") && t.CreatedAtUtc >= threshold)
            .OrderBy(t => t.CreatedAtUtc).ToListAsync(ct);

        var cups = tickets.SelectMany(t => t.Items.SelectMany(item => Enumerable.Range(0, item.Quantity)
            .Select(_ => (item.ProductName, Cup: new BatchCupItemDto(t.Id, t.OrderCode, t.TableNumber, item.Note, t.CreatedAtUtc))))).ToList();

        var batches = new List<BrewingBatchDto>();
        foreach (var product in cups.GroupBy(c => c.ProductName))
        {
            var list = product.Select(c => c.Cup).ToList();
            for (var i = 0; i < list.Count; i += maxBatchSize)
            {
                var chunk = list.Skip(i).Take(maxBatchSize).ToList();
                batches.Add(new BrewingBatchDto(product.Key, chunk.Count, maxBatchSize, chunk));
            }
        }

        return batches;
    }

    private static async Task<KitchenTicketDto> GetAsync(Guid id, KitchenOperationsDbContext db, ICurrentUser user, CancellationToken ct) =>
        TicketMapper.ToDto(await TicketAccess.LoadAsync(db, user, id, ct, track: false));

    /// <summary>
    /// Brewing, ReadyForPickup, Completed or Cancelled. Starting and finishing tell the order service (and the cashier screens, SCRUM-58).
    /// The cashier hands the order over through the order service, which completes the ticket; here the bar may complete it too.
    /// </summary>
    private static async Task<IResult> UpdateStatusAsync(
        Guid id,
        UpdateKitchenTicketStatusRequest r,
        KitchenOperationsDbContext db,
        ICurrentUser user,
        IKitchenNotifier notifier,
        IEventBus bus,
        TimeProvider clock,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var outcome = await KitchenTicketTransitions.UpdateAsync(id, r, db, user, now, ct);
        var ticket = outcome.Ticket;
        if (!outcome.Changed) return Results.Ok(TicketMapper.ToDto(ticket));
        var log = loggers.CreateLogger("AICOS.Kitchen.Tickets");
        await SafeAsync(log, () => notifier.NotifyStatusChangedAsync(ticket.TenantId, ticket.BranchId, ticket.Id, ticket.Status.ToString(), ct));

        if (ticket.Status == KitchenTicketStatus.Brewing)
        {
            await SafeAsync(log, () => bus.PublishAsync(new KitchenOrderStartedIntegrationEvent(ticket.TenantId, ticket.Id, ticket.OrderId, ticket.BranchId, ticket.BrewingStartedAtUtc ?? now), ct));
        }
        else if (ticket.Status == KitchenTicketStatus.ReadyForPickup)
        {
            var readyAt = ticket.ReadyAtUtc ?? now;
            var notification = new OrderReadyNotificationDto(
                ticket.Id, ticket.OrderId, ticket.OrderCode, ticket.BranchId, ticket.TableNumber, ticket.OrderType, ticket.TotalCups, readyAt,
                string.IsNullOrWhiteSpace(ticket.TableNumber)
                    ? $"Order {ticket.OrderCode} ({ticket.OrderType}) is ready!"
                    : $"Order {ticket.OrderCode} for table {ticket.TableNumber} is ready to serve!");
            await SafeAsync(log, () => notifier.NotifyOrderReadyForPickupAsync(ticket.TenantId, ticket.BranchId, notification, ct));
            await SafeAsync(log, () => bus.PublishAsync(new KitchenOrderReadyIntegrationEvent(ticket.TenantId, ticket.Id, ticket.OrderId, ticket.OrderCode, ticket.BranchId, ticket.TableNumber, readyAt), ct));
        }

        return Results.Ok(TicketMapper.ToDto(ticket));
    }

    /// <summary>
    /// The bar makes a ready order again ("pha lại"). The ticket goes back to the queue as Brewing, the cashier screens and the order service are told,
    /// and no new charge is made. Only before the hand-over.
    /// </summary>
    private static async Task<IResult> RemakeAsync(
        Guid id,
        RemakeTicketRequest r,
        KitchenOperationsDbContext db,
        ICurrentUser user,
        IKitchenNotifier notifier,
        IEventBus bus,
        TimeProvider clock,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var errors = new AICOS.BuildingBlocks.Validation.ValidationErrors();
        AICOS.BuildingBlocks.Validation.CommonRules.Required(errors, "Reason", r.Reason, "Reason", 200);
        errors.ThrowIfAny();

        var now = clock.GetUtcNow().UtcDateTime;
        var ticket = await KitchenTicketTransitions.RemakeAsync(id, r.Reason!, db, user, now, ct);

        var log = loggers.CreateLogger("AICOS.Kitchen.Tickets");
        await SafeAsync(log, () => notifier.NotifyStatusChangedAsync(ticket.TenantId, ticket.BranchId, ticket.Id, ticket.Status.ToString(), ct));
        await SafeAsync(log, () => bus.PublishAsync(
            new KitchenOrderRemakeIntegrationEvent(ticket.TenantId, ticket.Id, ticket.OrderId, ticket.BranchId, ticket.LastRemakeReason!, ticket.RemakeCount, now), ct));
        return Results.Ok(TicketMapper.ToDto(ticket));
    }

    /// <summary>The stickers of a ticket (FR-36); printing may be simulated, the client prints what it gets.</summary>
    private static async Task<object> StickersAsync(Guid id, KitchenOperationsDbContext db, ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        var hint = await TicketAccess.LoadAsync(db, user, id, ct, track: false);
        return await KitchenTicketWrites.RunAsync<object>(db, hint.TenantId, hint.OrderId, async () =>
        {
            var ticket = await TicketAccess.LoadAsync(db, user, id, ct);
            if (!ticket.TryGenerateCupStickers(clock.GetUtcNow().UtcDateTime, out var stickers))
            {
                throw new BusinessRuleException("ticket.not_paid", "BR-43: stickers are printed for a paid or dine-in order.");
            }

            await db.SaveChangesAsync(ct);
            var dtos = stickers.Select(TicketMapper.ToDto).ToList();
            return new { TicketId = ticket.Id, ticket.OrderCode, ticket.TableNumber, TotalStickers = dtos.Count, Stickers = dtos };
        }, ct);
    }

    internal static void RequireBar(ICurrentUser user)
    {
        if (!BarRoles.Any(user.IsInRole))
        {
            throw new ForbiddenException("Only the bar team changes how far an order is made.");
        }
    }

    /// <summary>The ticket change is already saved; a screen or message that fails must not undo it.</summary>
    internal static async Task SafeAsync(ILogger logger, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "A notification could not be sent; the ticket change was saved.");
        }
    }
}

internal static class TicketAccess
{
    /// <summary>404 for a ticket of another brand, 403 for another branch of the brand.</summary>
    public static async Task<KitchenTicket> LoadAsync(KitchenOperationsDbContext db, ICurrentUser user, Guid id, CancellationToken ct, bool track = true)
    {
        var tenantId = TenantScope.Require(user);
        var query = db.Tickets.Include(t => t.Items).Include(t => t.Stickers).AsSplitQuery().Where(t => t.Id == id && t.TenantId == tenantId);
        var ticket = await (track ? query : query.AsNoTracking()).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Ticket", id);
        BranchAccess.EnsureCanAccess(user, ticket.BranchId);
        return ticket;
    }
}

internal static class TicketMapper
{
    public static KitchenTicketDto ToDto(KitchenTicket t) => new(
        t.Id, t.OrderId, t.OrderCode, t.BranchId, t.TableNumber, t.OrderType, t.Status.ToString(), t.IsPaid, t.IsPriority, t.CreatedAtUtc, t.ScheduledPickupTimeUtc,
        t.BrewingStartedAtUtc, t.ReadyAtUtc, t.TotalCups,
        t.Items.Select(i => new KitchenTicketItemDto(i.ProductId, i.ProductName, i.Quantity, i.Note)).ToList(),
        t.Stickers.Select(ToDto).ToList(), t.RemakeCount, t.LastRemakeReason);

    public static CupStickerDto ToDto(CupSticker s) => new(
        s.Id, s.TicketId, s.OrderId, s.OrderCode, s.TableNumber, s.ProductName, s.CupIndex, s.TotalCupsInOrder, s.Customizations, s.PrintedAtUtc);
}
