using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Security;
using AICOS.BuildingBlocks.Validation;
using AICOS.Ordering.Api.Domain;
using AICOS.Ordering.Api.Domain.Entities;
using AICOS.Ordering.Api.Domain.Enums;
using AICOS.Ordering.Api.Features.PickupSlots;
using AICOS.Ordering.Api.Persistence;
using AICOS.Ordering.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace AICOS.Ordering.Api.Features.Orders;

/// <summary>
/// A customer orders takeaway for a pickup slot and rates the order afterwards (FR-17, UC-21, BR-22 to BR-24). Routes: /api/v1/orders.
/// The customer pays through the Payment service; an unpaid pickup order is released after <c>Ordering:Pickup:HoldMinutes</c>
/// (see <see cref="PickupOrderExpiryWorker"/>). The pickup hours are <c>Ordering:Pickup:OpenTime</c> and <c>CloseTime</c> in the shop time zone.
/// </summary>
public static class PickupOrderEndpoints
{
    public static void MapPickupOrderEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/pickup-slots/available", AvailableSlotsAsync).WithTags("Pickup orders").RequireAuthorization();
        api.MapPost("/pickup", CreateAsync).WithTags("Pickup orders").RequireAuthorization(b => b.RequireRole(AicosRoles.Customer));

        api.MapPost("/{id:guid}/feedback", SubmitFeedbackAsync).WithTags("Order feedback").RequireAuthorization(b => b.RequireRole(AicosRoles.Customer));
        api.MapGet("/{id:guid}/feedback", GetFeedbackAsync).WithTags("Order feedback").RequireAuthorization();
        api.MapGet("/feedbacks", ListFeedbacksAsync).WithTags("Order feedback").RequireAuthorization(AicosPolicies.Management);
    }

    // ------------------------------------------------------------------ slots
    private static async Task<AvailablePickupSlotsResponse> AvailableSlotsAsync(
        Guid? branchId, DateOnly? date, OrderingDbContext db, ICurrentUser user, IBranchDirectory directory, ShopTime clock, IConfiguration config, CancellationToken ct)
    {
        var tenantId = TenantScope.Require(user);
        var branch = branchId ?? user.BranchId ?? throw new ValidationFailedException("branchId", "branchId is required.");
        await BranchScope.EnsureUsableAsync(directory, branch, requireActive: false, ct);
        var day = date ?? clock.Today;
        var rules = await PickupSlotEndpoints.ForBranchAsync(db, tenantId, branch, ct);
        var hours = PickupHours.From(config);
        if (day < clock.Today || day > clock.Today.AddDays(rules.AdvanceDays))
        {
            return new AvailablePickupSlotsResponse(branch, day, rules.SlotMinutes, rules.CapacityPerSlot, rules.CutoffMinutes, []);
        }

        var booked = await BookedBySlotAsync(db, tenantId, branch, clock.StartOfDayUtc(day), clock.StartOfDayUtc(day.AddDays(1)), ct);
        var now = clock.UtcNow;
        var close = clock.ToUtc(day, hours.Close);
        var slots = new List<PickupSlotInfo>();
        for (var start = clock.ToUtc(day, hours.Open); start.AddMinutes(rules.SlotMinutes) <= close; start = start.AddMinutes(rules.SlotMinutes))
        {
            var count = booked.GetValueOrDefault(start);
            var locked = now >= start.AddMinutes(-rules.CutoffMinutes);
            var remaining = Math.Max(0, rules.CapacityPerSlot - count);
            slots.Add(new PickupSlotInfo(start, start.AddMinutes(rules.SlotMinutes), rules.CapacityPerSlot, count, remaining, locked, !locked && remaining > 0));
        }

        return new AvailablePickupSlotsResponse(branch, day, rules.SlotMinutes, rules.CapacityPerSlot, rules.CutoffMinutes, slots);
    }

    private static async Task<Dictionary<DateTime, int>> BookedBySlotAsync(OrderingDbContext db, Guid tenantId, Guid branchId, DateTime fromUtc, DateTime toUtc, CancellationToken ct) =>
        (await db.Orders.AsNoTracking()
            .Where(o => o.TenantId == tenantId && o.BranchId == branchId && o.PickupSlotStartUtc != null && o.PickupSlotStartUtc >= fromUtc && o.PickupSlotStartUtc < toUtc
                        && o.Status != OrderStatus.Cancelled)
            .GroupBy(o => o.PickupSlotStartUtc!.Value)
            .Select(g => new { Slot = g.Key, Count = g.Count() })
            .ToListAsync(ct))
        .ToDictionary(x => DateTime.SpecifyKind(x.Slot, DateTimeKind.Utc), x => x.Count);

    // ------------------------------------------------------------------ create
    private static async Task<IResult> CreateAsync(
        CreatePickupOrderRequest r,
        HttpContext http,
        OrderingDbContext db,
        ICurrentUser user,
        IBranchDirectory directory,
        IMenuCatalogClient menu,
        IVoucherClient vouchers,
        OrderEventPublisher events,
        ShopTime clock,
        IConfiguration config,
        CancellationToken ct)
    {
        var errors = new ValidationErrors();
        CommonRules.RequiredId(errors, "BranchId", r.BranchId, "Branch");
        if (r.SlotStartUtc is null)
        {
            errors.Add("SlotStartUtc", "Pick a pickup slot.");
        }

        CommonRules.MaxLength(errors, "Note", r.Note, "Note", Order.NoteMaxLength);
        CommonRules.MaxLength(errors, "PromotionCode", r.PromotionCode, "Promotion code", 40);
        OrderEndpoints.ValidateItems(errors, r.Items);
        var idempotencyKey = http.Request.Headers["Idempotency-Key"].FirstOrDefault()?.Trim();
        if (idempotencyKey is { Length: > 80 })
        {
            errors.Add("Idempotency-Key", "The key is at most 80 characters.");
        }

        errors.ThrowIfAny();

        var tenantId = TenantScope.Require(user);
        var customerId = user.UserId ?? throw new UnauthorizedException("Not signed in.");

        if (!string.IsNullOrEmpty(idempotencyKey))
        {
            var again = await db.Orders.Include(o => o.Items).ThenInclude(i => i.Options).AsSplitQuery()
                .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.CustomerId == customerId && o.IdempotencyKey == idempotencyKey, ct);
            if (again is not null)
            {
                return Results.Ok(OrderResponse.From(again));   // the app retried after a lost answer: hand back the same order
            }
        }

        var branchId = r.BranchId!.Value;
        await BranchScope.EnsureUsableAsync(directory, branchId, requireActive: true, ct);

        var now = clock.UtcNow;
        var rules = await PickupSlotEndpoints.ForBranchAsync(db, tenantId, branchId, ct);
        var slot = DateTime.SpecifyKind(r.SlotStartUtc!.Value, DateTimeKind.Utc);
        EnsureSlotIsValid(slot, rules, PickupHours.From(config), clock, now);

        var sellable = await menu.GetSellableProductsAsync(branchId, ct);
        var unavailable = r.Items!.Select(i => i.ProductId!.Value).Distinct().Where(id => !sellable.ContainsKey(id)).ToList();
        if (unavailable.Count > 0)
        {
            throw new BusinessRuleException("order.item_unavailable", $"These products are not on sale at this branch right now: {string.Join(", ", unavailable)}.");
        }

        var order = new Order(tenantId, branchId, OrderType.Takeaway, now, customerId: customerId, note: r.Note?.Trim());
        foreach (var item in r.Items!)
        {
            var product = sellable[item.ProductId!.Value];
            var priced = OrderOptions.Resolve(product, item.OptionIds);
            order.AddItem(product.Id, product.Name, product.Price + priced.Surcharge, item.Quantity!.Value, string.IsNullOrWhiteSpace(item.Note) ? null : item.Note.Trim(), priced.Options);
        }

        // Capacity first: a full slot must not use up a voucher.
        await EnsureRoomAsync(db, tenantId, branchId, slot, rules.CapacityPerSlot, ct);
        if (!string.IsNullOrWhiteSpace(r.PromotionCode))
        {
            var quote = await vouchers.RedeemAsync(r.PromotionCode.Trim(), order.OriginalAmount, order.OrderCode, branchId, customerId, ct);
            order.ApplyPromotion(quote.Code, quote.Discount);
        }

        order.SchedulePickup(slot, now.AddMinutes(PickupHours.HoldMinutes(config)), idempotencyKey);

        // BR-22: count and insert under one lock per branch and slot, so two customers cannot both take the last place.
        try
        {
            await SaveWithinCapacityAsync(db, order, rules.CapacityPerSlot, ct);
        }
        catch (ConflictException) when (order.PromotionCode is not null)
        {
            await events.CancelledAsync(order.AbandonBeforeSave("The pickup slot was full.", now), wasPaid: false, ct);   // gives the voucher back
            throw;
        }

        await events.CreatedAsync(order, ct);
        return Results.Created($"/api/v1/orders/{order.Id}", OrderResponse.From(order));
    }

    private static async Task EnsureRoomAsync(OrderingDbContext db, Guid tenantId, Guid branchId, DateTime slot, int capacity, CancellationToken ct)
    {
        var taken = await db.Orders.AsNoTracking().CountAsync(
            o => o.TenantId == tenantId && o.BranchId == branchId && o.PickupSlotStartUtc == slot && o.Status != OrderStatus.Cancelled, ct);
        if (taken >= capacity)
        {
            throw new ConflictException("This pickup slot is full. Please pick another one.", "pickup.slot_full");
        }
    }

    private static async Task SaveWithinCapacityAsync(OrderingDbContext db, Order order, int capacity, CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
            if (tx is not null)
            {
                var key = (long)HashCode.Combine(order.BranchId, order.PickupSlotStartUtc);
                await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", [key], ct);
            }

            await EnsureRoomAsync(db, order.TenantId, order.BranchId, order.PickupSlotStartUtc!.Value, capacity, ct);
            db.Orders.Add(order);
            await db.SaveChangesAsync(ct);
            if (tx is not null)
            {
                await tx.CommitAsync(ct);
            }
        });
    }

    /// <summary>The slot is one of the offered ones: on the grid, inside the pickup hours and the advance window, and not yet locked by the cut-off (BR-23).</summary>
    private static void EnsureSlotIsValid(DateTime slotUtc, EffectivePickupSlots rules, PickupHours hours, ShopTime clock, DateTime now)
    {
        var day = DateOnly.FromDateTime(clock.ToLocal(slotUtc));
        var open = clock.ToUtc(day, hours.Open);
        var close = clock.ToUtc(day, hours.Close);
        if (slotUtc < open || slotUtc.AddMinutes(rules.SlotMinutes) > close || (slotUtc - open).TotalMinutes % rules.SlotMinutes != 0)
        {
            throw new BusinessRuleException("pickup.invalid_slot", $"Pick one of the offered slots ({rules.SlotMinutes}-minute slots between {hours.Open:HH\\:mm} and {hours.Close:HH\\:mm}).");
        }

        if (day > clock.Today.AddDays(rules.AdvanceDays))
        {
            throw new BusinessRuleException("pickup.too_far_ahead", $"You can order at most {rules.AdvanceDays} day(s) ahead.");
        }

        if (now >= slotUtc.AddMinutes(-rules.CutoffMinutes))
        {
            throw new BusinessRuleException("pickup.slot_locked", $"BR-23: this slot is locked. Orders close {rules.CutoffMinutes} minutes before the slot starts.");
        }
    }

    // ------------------------------------------------------------------ feedback
    private static async Task<IResult> SubmitFeedbackAsync(Guid id, OrderFeedbackRequest r, OrderingDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        if (r.Rating is null or < 1 or > 5)
        {
            errors.Add("Rating", "The rating is between 1 and 5.");
        }

        CommonRules.MaxLength(errors, "Comment", r.Comment, "Comment", OrderFeedback.CommentMaxLength);
        errors.ThrowIfAny();

        var order = await OrderAccess.LoadAsync(db, user, id, ct, track: false);
        if (order.Status != OrderStatus.Completed)
        {
            throw new BusinessRuleException("feedback.order_not_completed", "You can rate an order once you have received it.");
        }

        if (await db.OrderFeedbacks.AnyAsync(f => f.OrderId == id, ct))
        {
            throw new ConflictException("You already rated this order.", "feedback.duplicate");
        }

        var feedback = new OrderFeedback(order.TenantId, order.BranchId, order.Id, order.OrderCode, user.UserId!.Value, r.Rating!.Value, r.Comment);
        db.OrderFeedbacks.Add(feedback);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.Entry(feedback).State = EntityState.Detached;
            if (await db.OrderFeedbacks.AsNoTracking().AnyAsync(f => f.OrderId == id, ct))
            {
                throw new ConflictException("You already rated this order.", "feedback.duplicate");   // two taps at once
            }

            throw;
        }

        return Results.Created($"/api/v1/orders/{id}/feedback", OrderFeedbackResponse.From(feedback));
    }

    private static async Task<OrderFeedbackResponse> GetFeedbackAsync(Guid id, OrderingDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var order = await OrderAccess.LoadAsync(db, user, id, ct, track: false);
        var feedback = await db.OrderFeedbacks.AsNoTracking().FirstOrDefaultAsync(f => f.OrderId == order.Id, ct) ?? throw new NotFoundException("Feedback", id);
        return OrderFeedbackResponse.From(feedback);
    }

    /// <summary>Manager view: the ratings of the brand, or of the own branch for a branch manager.</summary>
    private static async Task<List<OrderFeedbackResponse>> ListFeedbacksAsync(
        OrderingDbContext db, ICurrentUser user, Guid? branchId, int? minRating, int? maxRating, int? limit, CancellationToken ct)
    {
        var tenantId = TenantScope.Require(user);
        var query = db.OrderFeedbacks.AsNoTracking().Where(f => f.TenantId == tenantId);
        var branch = BranchAccess.ResolveFilter(user, branchId);
        if (branch is not null)
        {
            query = query.Where(f => f.BranchId == branch);
        }

        if (minRating is not null)
        {
            query = query.Where(f => f.Rating >= minRating);
        }

        if (maxRating is not null)
        {
            query = query.Where(f => f.Rating <= maxRating);
        }

        var rows = await query.OrderByDescending(f => f.CreatedAtUtc).Take(Math.Clamp(limit ?? 50, 1, OrderEndpoints.MaxPageSize)).ToListAsync(ct);
        return rows.Select(OrderFeedbackResponse.From).ToList();
    }
}

/// <summary>Pickup hours and the payment hold, from configuration (shop time zone).</summary>
public readonly record struct PickupHours(TimeOnly Open, TimeOnly Close)
{
    public static PickupHours From(IConfiguration config) => new(
        TimeOnly.TryParse(config["Ordering:Pickup:OpenTime"], out var open) ? open : new TimeOnly(7, 0),
        TimeOnly.TryParse(config["Ordering:Pickup:CloseTime"], out var close) ? close : new TimeOnly(22, 0));

    public static int HoldMinutes(IConfiguration config) =>
        int.TryParse(config["Ordering:Pickup:HoldMinutes"], out var minutes) && minutes is > 0 and <= 240 ? minutes : 15;
}

/// <summary>Releases pickup orders nobody paid in time: the slot is freed and the voucher goes back (through the cancelled event).</summary>
public sealed class PickupOrderExpiryWorker(IServiceScopeFactory scopes, TimeProvider clock, ILogger<PickupOrderExpiryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await ReleaseOverdueAsync(scope.ServiceProvider, clock.GetUtcNow().UtcDateTime, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Releasing overdue pickup orders failed; it is tried again in a minute.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public static async Task<int> ReleaseOverdueAsync(IServiceProvider services, DateTime now, CancellationToken ct)
    {
        var db = services.GetRequiredService<OrderingDbContext>();
        var events = services.GetRequiredService<OrderEventPublisher>();
        var overdue = await db.Orders.Include(o => o.Items).ThenInclude(i => i.Options).AsSplitQuery()
            .Where(o => o.PaymentDueAtUtc != null && o.PaymentDueAtUtc <= now && o.PaymentStatus == PaymentStatus.Unpaid && o.Status == OrderStatus.Pending)
            .OrderBy(o => o.PaymentDueAtUtc).Take(100).ToListAsync(ct);
        var released = 0;
        foreach (var order in overdue)
        {
            if (!order.ReleaseIfPaymentOverdue(now))
            {
                continue;
            }

            await db.SaveChangesAsync(ct);
            await events.CancelledAsync(order, wasPaid: false, ct);
            released++;
        }

        return released;
    }
}
