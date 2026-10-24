using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Security;
using AICOS.BuildingBlocks.Validation;
using AICOS.Ordering.Api.Domain.Entities;
using AICOS.Ordering.Api.Domain.Enums;
using AICOS.Ordering.Api.Persistence;
using AICOS.Ordering.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace AICOS.Ordering.Api.Features.Orders;

/// <summary>
/// Counter orders (POS): create, bill, hand over, cancel, and the manager exception path. Routes: /api/v1/orders.
/// Money is not taken here: the cashier records cash (or the customer pays online) in the Payment service, which tells this service with
/// <c>PaymentSucceededEvent</c>. Prices come from the Menu service. Every order belongs to a brand and a branch (BR-04, BR-05).
/// </summary>
public static class OrderEndpoints
{
    public const int MaxItems = 50;
    public const int MaxQuantity = 99;
    public const int MaxPageSize = 200;

    public static void MapOrderingEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup(string.Empty).WithTags("Orders");

        group.MapPost("/", CreateAsync).RequireAuthorization(b => b.RequireRole(OrderAccess.CounterRoles));
        group.MapGet("/", ListAsync).RequireAuthorization(AicosPolicies.Staff);
        group.MapGet("/mine", MineAsync).RequireAuthorization(b => b.RequireRole(AicosRoles.Customer));
        group.MapGet("/exceptions", ExceptionsAsync).RequireAuthorization(AicosPolicies.Management);
        group.MapGet("/by-code/{orderCode}/receipt", ReceiptByCodeAsync).RequireAuthorization(b => b.RequireRole(OrderAccess.CounterRoles));
        group.MapGet("/{id:guid}", GetAsync).RequireAuthorization();
        group.MapGet("/{id:guid}/payment", PaymentAsync).RequireAuthorization();
        group.MapGet("/{id:guid}/receipt", ReceiptAsync).RequireAuthorization(b => b.RequireRole(OrderAccess.CounterRoles));
        group.MapPut("/{id:guid}/service-type", SwitchServiceTypeAsync).RequireAuthorization(b => b.RequireRole(OrderAccess.CounterRoles));
        group.MapPost("/{id:guid}/cancel", CancelAsync).RequireAuthorization(b => b.RequireRole([.. OrderAccess.CounterRoles, AicosRoles.Customer]));
        group.MapPost("/{id:guid}/deliver", DeliverAsync).RequireAuthorization(b => b.RequireRole(OrderAccess.CounterRoles));
        group.MapPost("/{id:guid}/exception", ExceptionAsync).RequireAuthorization(AicosPolicies.Management);
    }

    // ------------------------------------------------------------------ create
    private static async Task<IResult> CreateAsync(
        CreateOrderRequest r,
        OrderingDbContext db,
        ICurrentUser user,
        IBranchDirectory directory,
        IMenuCatalogClient menu,
        IVoucherClient vouchers,
        OrderEventPublisher events,
        ShopTime clock,
        CancellationToken ct)
    {
        var errors = new ValidationErrors();
        CommonRules.RequiredEnum(errors, "Type", r.Type, "Order type");
        CommonRules.MaxLength(errors, "Note", r.Note, "Note", Order.NoteMaxLength);
        CommonRules.MaxLength(errors, "TableNumber", r.TableNumber, "Table number", 20);
        CommonRules.MaxLength(errors, "DeliveryAddress", r.DeliveryAddress, "Delivery address", 300);
        CommonRules.MaxLength(errors, "PromotionCode", r.PromotionCode, "Promotion code", 40);
        ValidateItems(errors, r.Items);

        if (r.Type == OrderType.Delivery && string.IsNullOrWhiteSpace(r.DeliveryAddress))
        {
            errors.Add("DeliveryAddress", "A delivery order needs an address.");
        }

        errors.ThrowIfAny();

        var tenantId = TenantScope.Require(user);
        var branchId = await BranchScope.ResolveAsync(user, directory, r.BranchId, requireActive: true, ct);

        var sellable = await menu.GetSellableProductsAsync(branchId, ct);
        var unavailable = r.Items!.Select(i => i.ProductId!.Value).Distinct().Where(id => !sellable.ContainsKey(id)).ToList();
        if (unavailable.Count > 0)
        {
            throw new BusinessRuleException("order.item_unavailable", $"These products are not on sale at this branch right now: {string.Join(", ", unavailable)}.");
        }

        var order = new Order(tenantId, branchId, r.Type!.Value, clock.UtcNow, cashierId: user.UserId, customerId: r.CustomerId, tableId: r.TableId, tableNumber: r.TableNumber,
            deliveryAddress: r.DeliveryAddress, paymentMethod: r.PaymentMethod, note: r.Note?.Trim());
        foreach (var item in r.Items!)
        {
            var product = sellable[item.ProductId!.Value];
            order.AddItem(product.Id, product.Name, product.Price, item.Quantity!.Value, string.IsNullOrWhiteSpace(item.Note) ? null : item.Note.Trim());
        }

        if (!string.IsNullOrWhiteSpace(r.PromotionCode))
        {
            var quote = await vouchers.RedeemAsync(r.PromotionCode.Trim(), order.OriginalAmount, order.OrderCode, branchId, r.CustomerId, ct);
            order.ApplyPromotion(quote.Code, quote.Discount);
        }

        db.Orders.Add(order);
        await db.SaveChangesAsync(ct);
        await events.CreatedAsync(order, ct);

        return Results.Created($"/api/v1/orders/{order.Id}", OrderResponse.From(order));
    }

    /// <summary>The same rules for every way of placing an order: 1 to 50 lines, quantity 1 to 99, a product on each line.</summary>
    internal static void ValidateItems(ValidationErrors errors, List<OrderItemRequest>? items)
    {
        if (items is null || items.Count == 0)
        {
            errors.Add("Items", "An order needs at least one item.");
        }
        else if (items.Count > MaxItems)
        {
            errors.Add("Items", $"An order has at most {MaxItems} lines.");
        }
        else
        {
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item.ProductId is null || item.ProductId == Guid.Empty)
                {
                    errors.Add($"Items[{i}].ProductId", "Product is required.");
                }

                if (item.Quantity is null or < 1 or > MaxQuantity)
                {
                    errors.Add($"Items[{i}].Quantity", $"Quantity must be between 1 and {MaxQuantity}.");
                }

                CommonRules.MaxLength(errors, $"Items[{i}].Note", item.Note, "Item note", 300);
            }
        }
    }

    // ------------------------------------------------------------------ read
    private static async Task<List<OrderSummary>> ListAsync(
        OrderingDbContext db,
        ICurrentUser user,
        ShopTime clock,
        Guid? branchId,
        OrderStatus? status,
        PaymentStatus? paymentStatus,
        DateOnly? from,
        DateOnly? to,
        bool? includePaid,
        int? limit,
        int? offset,
        CancellationToken ct)
    {
        var tenantId = TenantScope.Require(user);
        var query = db.Orders.AsNoTracking().Include(o => o.Items).Where(o => o.TenantId == tenantId);
        var branch = BranchAccess.ResolveFilter(user, branchId);
        if (branch is not null)
        {
            query = query.Where(o => o.BranchId == branch);
        }

        if (status is not null)
        {
            query = query.Where(o => o.Status == status);
        }

        if (paymentStatus is not null)
        {
            query = query.Where(o => o.PaymentStatus == paymentStatus);
        }

        query = ApplyRange(query, clock, from, to, includePaid == true);

        var orders = await query.OrderByDescending(o => o.CreatedAtUtc)
            .Skip(Math.Max(0, offset ?? 0)).Take(Math.Clamp(limit ?? 50, 1, MaxPageSize)).AsSplitQuery().ToListAsync(ct);
        return orders.Select(OrderSummary.From).ToList();
    }

    private static async Task<List<OrderSummary>> MineAsync(OrderingDbContext db, ICurrentUser user, int? limit, CancellationToken ct)
    {
        var tenantId = TenantScope.Require(user);
        var orders = await db.Orders.AsNoTracking().Include(o => o.Items)
            .Where(o => o.TenantId == tenantId && o.CustomerId == user.UserId)
            .OrderByDescending(o => o.CreatedAtUtc).Take(Math.Clamp(limit ?? 50, 1, MaxPageSize)).AsSplitQuery().ToListAsync(ct);
        return orders.Select(OrderSummary.From).ToList();
    }

    private static async Task<OrderResponse> GetAsync(Guid id, OrderingDbContext db, ICurrentUser user, CancellationToken ct) =>
        OrderResponse.From(await OrderAccess.LoadAsync(db, user, id, ct, track: false));

    /// <summary>Checks the payment of an order (SCRUM-60): what Payment reported, nothing more.</summary>
    private static async Task<PaymentVerificationResponse> PaymentAsync(Guid id, OrderingDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var o = await OrderAccess.LoadAsync(db, user, id, ct, track: false);
        return new PaymentVerificationResponse(o.Id, o.OrderCode, o.FinalAmount, o.PaymentStatus, o.PaymentMethod, o.PaymentProvider, o.PaymentId, o.PaidAtUtc, o.RefundedAmount, o.IsPaid);
    }

    // ------------------------------------------------------------------ bill (SCRUM-60)
    private static async Task<ReceiptResponse> ReceiptAsync(Guid id, OrderingDbContext db, ICurrentUser user, ShopTime clock, CancellationToken ct)
    {
        var order = await OrderAccess.LoadAsync(db, user, id, ct);
        return await PrintAsync(db, order, clock, ct);
    }

    private static async Task<ReceiptResponse> ReceiptByCodeAsync(string orderCode, OrderingDbContext db, ICurrentUser user, ShopTime clock, CancellationToken ct)
    {
        var tenantId = TenantScope.Require(user);
        var order = await db.Orders.Include(o => o.Items).AsSplitQuery().FirstOrDefaultAsync(o => o.TenantId == tenantId && o.OrderCode == orderCode, ct)
            ?? throw new NotFoundException("Order", orderCode);
        OrderAccess.Ensure(user, order);
        return await PrintAsync(db, order, clock, ct);
    }

    private static async Task<ReceiptResponse> PrintAsync(OrderingDbContext db, Order order, ShopTime clock, CancellationToken ct)
    {
        order.MarkAsPrinted(clock.UtcNow);
        await db.SaveChangesAsync(ct);
        return ReceiptResponse.From(order, clock.UtcNow);
    }

    // ------------------------------------------------------------------ change
    private static async Task<OrderResponse> SwitchServiceTypeAsync(
        Guid id, SwitchServiceTypeRequest r, OrderingDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        CommonRules.RequiredEnum(errors, "Type", r.Type, "Order type");
        CommonRules.MaxLength(errors, "TableNumber", r.TableNumber, "Table number", 20);
        CommonRules.MaxLength(errors, "DeliveryAddress", r.DeliveryAddress, "Delivery address", 300);
        if (r.Type == OrderType.Delivery && string.IsNullOrWhiteSpace(r.DeliveryAddress))
        {
            errors.Add("DeliveryAddress", "A delivery order needs an address.");
        }

        errors.ThrowIfAny();

        var order = await OrderAccess.LoadAsync(db, user, id, ct);
        order.SwitchServiceType(r.Type!.Value, r.TableId, r.TableNumber, r.DeliveryAddress);
        await db.SaveChangesAsync(ct);
        return OrderResponse.From(order);
    }

    /// <summary>BR-04: an unpaid order can be cancelled by the counter. A paid one needs the manager exception path.</summary>
    private static async Task<OrderResponse> CancelAsync(
        Guid id, CancelOrderRequest r, OrderingDbContext db, ICurrentUser user, IPaymentClient payments, OrderEventPublisher events, ShopTime clock, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        CommonRules.MaxLength(errors, "Reason", r.Reason, "Reason", 300);
        errors.ThrowIfAny();

        var order = await OrderAccess.LoadAsync(db, user, id, ct);
        if (user.IsInRole(AicosRoles.Customer))
        {
            return await CancelAsCustomerAsync(order, r.Reason, db, payments, events, clock, ct);
        }

        order.CancelOrder(r.Reason ?? string.Empty, clock.UtcNow);
        await db.SaveChangesAsync(ct);
        await events.CancelledAsync(order, wasPaid: false, ct);
        return OrderResponse.From(order);
    }

    /// <summary>
    /// A customer cancels their own order (BR-24): before the bar starts it and, for a pickup order, before the slot is locked. A paid order is
    /// refunded by the brand cancellation policy; when the policy gives nothing back, Payment refuses and the order stays as it is.
    /// </summary>
    private static async Task<OrderResponse> CancelAsCustomerAsync(
        Order order, string? reason, OrderingDbContext db, IPaymentClient payments, OrderEventPublisher events, ShopTime clock, CancellationToken ct)
    {
        var now = clock.UtcNow;
        DateTime? lockedAt = null;
        if (order.PickupSlotStartUtc is not null)
        {
            var rules = await PickupSlots.PickupSlotEndpoints.ForBranchAsync(db, order.TenantId, order.BranchId, ct);
            lockedAt = order.PickupSlotStartUtc.Value.AddMinutes(-rules.CutoffMinutes);
        }

        order.EnsureCustomerCanCancel(now, lockedAt);
        var text = string.IsNullOrWhiteSpace(reason) ? "Cancelled by the customer." : reason.Trim();
        if (order.IsPaid)
        {
            if (order.PaymentId is null)
            {
                throw new BusinessRuleException("order.not_refundable", "This order has no payment to refund; please ask the staff.");
            }

            await payments.RequestRefundAsync(order.PaymentId.Value, "Cancellation", null, text, ct);
            await db.Entry(order).ReloadAsync(ct);   // the refund event may already have reached this service
        }

        var wasPaid = order.CancelByCustomer(text, now, lockedAt);
        await db.SaveChangesAsync(ct);
        await events.CancelledAsync(order, wasPaid, ct);
        return OrderResponse.From(order);
    }

    /// <summary>Staff confirm the customer received the order (SCRUM-61).</summary>
    private static async Task<OrderResponse> DeliverAsync(
        Guid id, OrderingDbContext db, ICurrentUser user, OrderEventPublisher events, ShopTime clock, CancellationToken ct)
    {
        var order = await OrderAccess.LoadAsync(db, user, id, ct);
        order.Deliver(user.UserId, clock.UtcNow);
        await db.SaveChangesAsync(ct);
        if (order.Status == OrderStatus.Completed)
        {
            await events.CompletedAsync(order, ct);
        }

        return OrderResponse.From(order);
    }

    // ------------------------------------------------------------------ exceptions (FR-30)
    /// <summary>
    /// A manager cancels a paid order and asks for the money back according to the brand policy (SCRUM-49). The refund request goes to the
    /// Payment service; this order tracks it as RefundPending, then Refunded or RefundFailed.
    /// </summary>
    private static async Task<OrderResponse> ExceptionAsync(
        Guid id,
        OrderExceptionRequest r,
        OrderingDbContext db,
        ICurrentUser user,
        IPaymentClient payments,
        OrderEventPublisher events,
        ShopTime clock,
        CancellationToken ct)
    {
        var errors = new ValidationErrors();
        CommonRules.Required(errors, "Reason", r.Reason, "Reason", 300);
        var kind = string.IsNullOrWhiteSpace(r.RefundKind) ? "Cancellation" : r.RefundKind.Trim();
        if (!new[] { "Cancellation", "NoShow", "Exception" }.Contains(kind, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add("RefundKind", "RefundKind must be Cancellation, NoShow or Exception.");
        }
        else if (kind.Equals("Exception", StringComparison.OrdinalIgnoreCase) && r.Amount is null)
        {
            errors.Add("Amount", "An exception refund needs an amount.");
        }

        if (r.Amount is <= 0)
        {
            errors.Add("Amount", "Amount must be greater than 0.");
        }

        errors.ThrowIfAny();

        var order = await OrderAccess.LoadAsync(db, user, id, ct);
        if (order.Status == OrderStatus.Cancelled)
        {
            throw new ConflictException("This order is already cancelled.", "order.already_cancelled");
        }

        if (!order.IsPaid || order.PaymentId is null)
        {
            throw new BusinessRuleException("order.not_paid", "Only a paid order is handled as an exception; cancel an unpaid order normally.");
        }

        await payments.RequestRefundAsync(order.PaymentId.Value, kind, r.Amount, r.Reason!.Trim(), ct);

        // The refund event may already have reached this service and updated the order while the request was open: look at the order as it is now.
        await db.Entry(order).ReloadAsync(ct);
        order.CancelPaidOrderAsException(r.Reason!, user.UserId, clock.UtcNow);
        await db.SaveChangesAsync(ct);
        await events.CancelledAsync(order, wasPaid: true, ct);
        return OrderResponse.From(order);
    }

    /// <summary>Orders that were cancelled after payment and where money has to go back or went back, with the state of the refund.</summary>
    private static async Task<List<OrderSummary>> ExceptionsAsync(
        OrderingDbContext db, ICurrentUser user, ShopTime clock, Guid? branchId, RefundStatus? refundStatus, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var tenantId = TenantScope.Require(user);
        var query = db.Orders.AsNoTracking().Include(o => o.Items).Where(o => o.TenantId == tenantId && o.RefundStatus != RefundStatus.None);
        var branch = BranchAccess.ResolveFilter(user, branchId);
        if (branch is not null)
        {
            query = query.Where(o => o.BranchId == branch);
        }

        query = ApplyRange(query, clock, from, to, false);
        var orders = await query.OrderByDescending(o => o.CreatedAtUtc).Take(MaxPageSize).AsSplitQuery().ToListAsync(ct);
        return orders.Where(o => refundStatus is null || o.EffectiveRefundStatus == refundStatus).Select(OrderSummary.From).ToList();
    }

    /// <summary>Orders created in the period; with <paramref name="includePaid"/> also those paid in it (used by reconciliation with Payment).</summary>
    private static IQueryable<Order> ApplyRange(IQueryable<Order> query, ShopTime clock, DateOnly? from, DateOnly? to, bool includePaid)
    {
        var start = from is null ? DateTime.MinValue : clock.StartOfDayUtc(from.Value);
        var end = to is null ? DateTime.MaxValue : clock.StartOfDayUtc(to.Value.AddDays(1));
        if (from is null && to is null)
        {
            return query;
        }

        return includePaid
            ? query.Where(o => (o.CreatedAtUtc >= start && o.CreatedAtUtc < end) || (o.PaidAtUtc != null && o.PaidAtUtc >= start && o.PaidAtUtc < end))
            : query.Where(o => o.CreatedAtUtc >= start && o.CreatedAtUtc < end);
    }
}
