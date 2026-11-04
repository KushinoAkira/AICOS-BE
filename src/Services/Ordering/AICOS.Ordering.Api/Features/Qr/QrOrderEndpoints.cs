using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.BuildingBlocks.Validation;
using AICOS.Ordering.Api.Domain;
using AICOS.Ordering.Api.Domain.Entities;
using AICOS.Ordering.Api.Domain.Enums;
using AICOS.Ordering.Api.Features.Orders;
using AICOS.Ordering.Api.Persistence;
using AICOS.Ordering.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace AICOS.Ordering.Api.Features.Qr;

public sealed record ScanQrRequest(string? QrToken);

public sealed record ScanQrResponse(
    string SessionToken, Guid SessionId, Guid TenantId, Guid BranchId, Guid TableId, string TableCode, string AreaName, DateTime OpenedAtUtc);

public sealed record AddQrCartItemRequest(Guid? ProductId, int? Quantity, string? Note, List<Guid>? OptionIds);

public sealed record SetQrCartQuantityRequest(int? Quantity);

public sealed record QrCheckoutRequest(string? Note);

/// <summary>The guests ask staff to come for the bill. <c>Method</c> is Cash or Transfer.</summary>
public sealed record QrBillRequest(string? Method);

/// <summary>A short-lived token that lets the people at this table pay the orders of this table online, and nothing else.</summary>
public sealed record QrPaymentAccessResponse(string AccessToken, DateTime ExpiresAtUtc, Guid BranchId);

public sealed record QrOrderStatus(Guid Id, string OrderCode, OrderStatus Status, PaymentStatus PaymentStatus, int ItemCount, decimal FinalAmount, DateTime CreatedAtUtc, DateTime? ReadyAtUtc, DateTime? DeliveredAtUtc);

public sealed record QrBillOrder(
    Guid OrderId, string OrderCode, OrderStatus Status, PaymentStatus PaymentStatus, DateTime CreatedAtUtc, decimal FinalAmount, IReadOnlyList<OrderItemResponse> Items,
    Guid? CashierId = null, Guid? CustomerId = null, DateTime? ReadyAtUtc = null, DateTime? DeliveredAtUtc = null);

/// <summary>The bill of the table: every round of the session. Cancelled orders are listed but not counted; <c>Due</c> is what is still unpaid.</summary>
public sealed record QrBillResponse(
    Guid SessionId, string TableCode, TableSessionStatus Status, IReadOnlyList<QrBillOrder> Orders, decimal Total, decimal Paid, decimal Due)
{
    public static QrBillResponse From(TableSession session, IReadOnlyList<Order> orders)
    {
        var live = orders.Where(o => !o.IsVoided).ToList();
        var total = live.Sum(o => o.FinalAmount);
        var paid = live.Where(o => o.IsPaid).Sum(o => o.FinalAmount);
        return new QrBillResponse(
            session.Id, session.TableCode, session.Status,
            orders.OrderBy(o => o.CreatedAtUtc).Select(o => new QrBillOrder(
                o.Id, o.OrderCode, o.Status, o.PaymentStatus, o.CreatedAtUtc, o.FinalAmount, o.Items.Select(OrderItemResponse.From).ToList(),
                o.CashierId, o.CustomerId, o.ReadyAtUtc, o.DeliveredAtUtc)).ToList(),
            total, paid, total - paid);
    }
}

public sealed record TableSessionSummary(
    Guid Id, Guid BranchId, Guid TableId, string TableCode, TableSessionStatus Status, DateTime OpenedAtUtc, DateTime LastActivityAtUtc, int Orders, decimal Due,
    DateTime? BillRequestedAtUtc, string? BillRequestedMethod);

/// <summary>
/// Ordering from the table QR code, without an account (UC-49, FR-14). Routes: /api/v1/orders/qr. Everyone who scans the code of a table joins one
/// session and shares one cart (kept in Redis) and one bill. The guest identifies the session with the <c>X-Table-Session</c> header, a token that
/// only a scan of the real code returns. The price always comes from the Menu service. The guests ask staff for the bill (cash or transfer), or pay
/// online themselves with a short-lived token that is limited to the orders of this table.
/// A member who signs in (Customer token) earns points: the order is linked to them.
/// </summary>
public static class QrOrderEndpoints
{
    public const string SessionHeader = "X-Table-Session";

    public static void MapQrOrderEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/qr").WithTags("Table QR ordering");

        group.MapPost("/scan", ScanAsync).AllowAnonymous();
        group.MapGet("/cart", GetCartAsync).AllowAnonymous();
        group.MapPost("/cart/items", AddItemAsync).AllowAnonymous();
        group.MapPut("/cart/items/{lineId:guid}", SetQuantityAsync).AllowAnonymous();
        group.MapDelete("/cart/items/{lineId:guid}", RemoveItemAsync).AllowAnonymous();
        group.MapDelete("/cart", ClearCartAsync).AllowAnonymous();
        group.MapPost("/checkout", CheckoutAsync).AllowAnonymous();
        group.MapGet("/orders", OrdersAsync).AllowAnonymous();
        group.MapGet("/session/bill", BillAsync).AllowAnonymous();
        group.MapPost("/session/bill-request", RequestBillAsync).AllowAnonymous();
        group.MapPost("/session/payment-access", PaymentAccessAsync).AllowAnonymous();

        group.MapGet("/sessions", ListSessionsAsync).RequireAuthorization(AicosPolicies.Staff);
        group.MapGet("/sessions/{id:guid}/bill", StaffBillAsync).RequireAuthorization(AicosPolicies.Staff);
        group.MapPost("/sessions/{id:guid}/close", CloseSessionAsync).RequireAuthorization(b => b.RequireRole(OrderAccess.CounterRoles));
    }

    // ------------------------------------------------------------------ the session
    private static async Task<TableSession> SessionAsync(
        HttpContext http, OrderingDbContext db, GuestSessionTokens tokens, ShopTime clock, bool mustBeOpen, CancellationToken ct)
    {
        if (!tokens.TryRead(http.Request.Headers[SessionHeader].FirstOrDefault(), out var id))
        {
            throw new UnauthorizedException($"Scan the table QR code first (send the {SessionHeader} header).", "qr.session_invalid");
        }

        var session = await db.TableSessions.FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw new UnauthorizedException("This table session does not exist. Please scan the QR code again.", "qr.session_invalid");
        if (mustBeOpen && !session.IsOpen)
        {
            throw new ConflictException("This table session is closed. Please scan the QR code again to order more.", "qr.session_closed");
        }

        return session;
    }

    /// <summary>Remembers that the table is in use. At most one write a minute, so a busy cart does not turn into database traffic.</summary>
    private static async Task TouchAsync(OrderingDbContext db, TableSession session, ShopTime clock, CancellationToken ct)
    {
        if (clock.UtcNow - session.LastActivityAtUtc >= TimeSpan.FromMinutes(1))
        {
            session.Touch(clock.UtcNow);
            await db.SaveChangesAsync(ct);
        }
    }

    private static async Task<IResult> ScanAsync(
        ScanQrRequest r, OrderingDbContext db, ITableQrClient tables, GuestSessionTokens tokens, ShopTime clock, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        CommonRules.Required(errors, "QrToken", r.QrToken, "QR code", 200);
        errors.ThrowIfAny();

        var table = await tables.LookupAsync(r.QrToken!, ct) ?? throw new NotFoundException("QR code", "scanned");
        if (!table.CanOrder)
        {
            throw new BusinessRuleException("qr.ordering_closed", "This table cannot take orders right now. Please ask the staff.");
        }

        var session = await db.TableSessions.FirstOrDefaultAsync(s => s.TenantId == table.TenantId && s.TableId == table.TableId && s.Status == TableSessionStatus.Open, ct);
        if (session is null)
        {
            session = new TableSession(table.TenantId, table.BranchId, table.TableId, table.TableCode, clock.UtcNow);
            db.TableSessions.Add(session);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Two phones scanned at the same moment: the other one opened the session first.
                db.Entry(session).State = EntityState.Detached;
                session = await db.TableSessions.FirstOrDefaultAsync(s => s.TenantId == table.TenantId && s.TableId == table.TableId && s.Status == TableSessionStatus.Open, ct)
                    ?? throw new DependencyUnavailableException("ordering");
            }
        }
        else
        {
            await TouchAsync(db, session, clock, ct);
        }

        return Results.Ok(new ScanQrResponse(tokens.Create(session.Id), session.Id, session.TenantId, session.BranchId, session.TableId, session.TableCode, table.AreaName, session.OpenedAtUtc));
    }

    // ------------------------------------------------------------------ the cart
    private static async Task<QrCartView> ViewAsync(TableSession session, QrCartStore cart, IMenuCatalogClient menu, CancellationToken ct) =>
        QrCartPricing.Price(session.Id, await cart.GetAsync(session.TenantId, session.Id, ct), await menu.GetSellableProductsAsync(session.BranchId, ct));

    private static async Task<QrCartView> GetCartAsync(
        HttpContext http, OrderingDbContext db, GuestSessionTokens tokens, ShopTime clock, QrCartStore cart, IMenuCatalogClient menu, CancellationToken ct) =>
        await ViewAsync(await SessionAsync(http, db, tokens, clock, mustBeOpen: false, ct), cart, menu, ct);

    private static async Task<QrCartView> AddItemAsync(
        AddQrCartItemRequest r, HttpContext http, OrderingDbContext db, GuestSessionTokens tokens, ShopTime clock, QrCartStore cart, IMenuCatalogClient menu, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        CommonRules.RequiredId(errors, "ProductId", r.ProductId, "Product");
        if (r.Quantity is null or < 1 or > QrCartStore.MaxQuantity)
        {
            errors.Add("Quantity", $"Quantity must be between 1 and {QrCartStore.MaxQuantity}.");
        }

        CommonRules.MaxLength(errors, "Note", r.Note, "Item note", 300);
        errors.ThrowIfAny();

        var session = await SessionAsync(http, db, tokens, clock, mustBeOpen: true, ct);
        var sellable = await menu.GetSellableProductsAsync(session.BranchId, ct);
        if (!sellable.TryGetValue(r.ProductId!.Value, out var product))
        {
            throw new BusinessRuleException("qr.item_unavailable", "This item is not on sale at this branch right now.");
        }

        OrderOptions.Resolve(product, r.OptionIds);   // refuse a bad choice now, not at checkout
        await cart.AddAsync(session.TenantId, session.Id, product.Id, r.Quantity!.Value, string.IsNullOrWhiteSpace(r.Note) ? null : r.Note.Trim(), r.OptionIds, ct);
        await TouchAsync(db, session, clock, ct);
        return QrCartPricing.Price(session.Id, await cart.GetAsync(session.TenantId, session.Id, ct), sellable);
    }

    private static async Task<QrCartView> SetQuantityAsync(
        Guid lineId, SetQrCartQuantityRequest r, HttpContext http, OrderingDbContext db, GuestSessionTokens tokens, ShopTime clock, QrCartStore cart, IMenuCatalogClient menu, CancellationToken ct)
    {
        if (r.Quantity is null or < 0 or > QrCartStore.MaxQuantity)
        {
            throw new ValidationFailedException("Quantity", $"Quantity must be between 0 (remove) and {QrCartStore.MaxQuantity}.");
        }

        var session = await SessionAsync(http, db, tokens, clock, mustBeOpen: true, ct);
        if (!await cart.SetQuantityAsync(session.TenantId, session.Id, lineId, r.Quantity.Value, ct))
        {
            throw new NotFoundException("Cart item", lineId);
        }

        await TouchAsync(db, session, clock, ct);
        return await ViewAsync(session, cart, menu, ct);
    }

    private static async Task<QrCartView> RemoveItemAsync(
        Guid lineId, HttpContext http, OrderingDbContext db, GuestSessionTokens tokens, ShopTime clock, QrCartStore cart, IMenuCatalogClient menu, CancellationToken ct)
    {
        var session = await SessionAsync(http, db, tokens, clock, mustBeOpen: true, ct);
        await cart.SetQuantityAsync(session.TenantId, session.Id, lineId, 0, ct);
        return await ViewAsync(session, cart, menu, ct);
    }

    private static async Task<IResult> ClearCartAsync(HttpContext http, OrderingDbContext db, GuestSessionTokens tokens, ShopTime clock, QrCartStore cart, CancellationToken ct)
    {
        var session = await SessionAsync(http, db, tokens, clock, mustBeOpen: true, ct);
        await cart.ClearAsync(session.TenantId, session.Id, ct);
        return Results.NoContent();
    }

    // ------------------------------------------------------------------ checkout
    /// <summary>
    /// The cart becomes an order of the session (a second and third round are further orders). The cart is taken atomically, so a double tap or two
    /// phones pressing "order" at once make one order. When something in the cart cannot be ordered the cart is put back and nothing is created.
    /// </summary>
    private static async Task<IResult> CheckoutAsync(
        QrCheckoutRequest r,
        HttpContext http,
        OrderingDbContext db,
        ICurrentUser user,
        GuestSessionTokens tokens,
        ShopTime clock,
        QrCartStore cart,
        IMenuCatalogClient menu,
        OrderEventPublisher events,
        CancellationToken ct)
    {
        var errors = new ValidationErrors();
        CommonRules.MaxLength(errors, "Note", r.Note, "Note", Order.NoteMaxLength);
        errors.ThrowIfAny();

        var session = await SessionAsync(http, db, tokens, clock, mustBeOpen: true, ct);
        var lines = await cart.TakeAsync(session.TenantId, session.Id, ct);
        if (lines.Count == 0)
        {
            throw new BusinessRuleException("qr.cart_empty", "The cart is empty, or this order was already sent.");
        }

        Order order;
        try
        {
            var sellable = await menu.GetSellableProductsAsync(session.BranchId, ct);
            var view = QrCartPricing.Price(session.Id, lines, sellable);
            if (view.HasProblems)
            {
                throw new BusinessRuleException(
                    "qr.cart_has_problems",
                    $"Some items cannot be ordered right now: {string.Join(", ", view.Lines.Where(l => l.Problem is not null).Select(l => $"{l.ProductName} ({l.Problem})"))}. Please check your cart.");
            }

            var memberId = user.IsInRole(AicosRoles.Customer) && user.TenantId == session.TenantId ? user.UserId : null;
            order = new Order(session.TenantId, session.BranchId, OrderType.DineIn, clock.UtcNow, customerId: memberId, tableId: session.TableId, tableNumber: session.TableCode, note: r.Note?.Trim());
            order.AttachToTableSession(session.Id);
            foreach (var line in lines)
            {
                var product = sellable[line.ProductId];
                var priced = OrderOptions.Resolve(product, line.OptionIds);
                order.AddItem(product.Id, product.Name, product.Price + priced.Surcharge, line.Quantity, line.Note, priced.Options);
            }

            db.Orders.Add(order);
            session.Touch(clock.UtcNow);
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            await cart.RestoreAsync(session.TenantId, session.Id, lines, ct);
            throw;
        }

        await events.CreatedAsync(order, ct);
        return Results.Created($"/api/v1/orders/qr/orders", OrderResponse.From(order));
    }

    // ------------------------------------------------------------------ what the table sees afterwards
    private static async Task<List<QrOrderStatus>> OrdersAsync(HttpContext http, OrderingDbContext db, GuestSessionTokens tokens, ShopTime clock, CancellationToken ct)
    {
        var session = await SessionAsync(http, db, tokens, clock, mustBeOpen: false, ct);
        var orders = await db.Orders.AsNoTracking().Include(o => o.Items).Where(o => o.TableSessionId == session.Id).OrderBy(o => o.CreatedAtUtc).ToListAsync(ct);
        return orders.Select(o => new QrOrderStatus(o.Id, o.OrderCode, o.Status, o.PaymentStatus, o.Items.Count, o.FinalAmount, o.CreatedAtUtc, o.ReadyAtUtc, o.DeliveredAtUtc)).ToList();
    }

    private static async Task<QrBillResponse> BillAsync(HttpContext http, OrderingDbContext db, GuestSessionTokens tokens, ShopTime clock, CancellationToken ct) =>
        await BuildBillAsync(db, await SessionAsync(http, db, tokens, clock, mustBeOpen: false, ct), ct);

    private static async Task<QrBillResponse> BuildBillAsync(OrderingDbContext db, TableSession session, CancellationToken ct)
    {
        var orders = await db.Orders.AsNoTracking().Include(o => o.Items).ThenInclude(i => i.Options).AsSplitQuery()
            .Where(o => o.TableSessionId == session.Id).ToListAsync(ct);
        return QrBillResponse.From(session, orders);
    }

    // ------------------------------------------------------------------ paying from the table
    /// <summary>Tells the counter that this table wants to pay. Asking again only updates the method, so a second tap is harmless.</summary>
    private static async Task<IResult> RequestBillAsync(
        QrBillRequest r, HttpContext http, OrderingDbContext db, GuestSessionTokens tokens, ShopTime clock, OrderEventPublisher events, CancellationToken ct)
    {
        var method = new[] { "Cash", "Transfer" }.FirstOrDefault(m => string.Equals(m, r.Method?.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new ValidationFailedException("Method", "Method must be Cash or Transfer.");
        var session = await SessionAsync(http, db, tokens, clock, mustBeOpen: true, ct);
        var bill = await BuildBillAsync(db, session, ct);
        if (bill.Due <= 0)
        {
            throw new BusinessRuleException("qr.nothing_to_pay", "This table has nothing to pay.");
        }

        session.RequestBill(method, clock.UtcNow);
        await db.SaveChangesAsync(ct);
        await events.BillRequestedAsync(session, method, ct);
        return Results.NoContent();
    }

    /// <summary>The Payment service needs a signed-in caller. This token names the session as the caller: <see cref="OrderAccess"/> lets it read only this table's orders.</summary>
    private static async Task<QrPaymentAccessResponse> PaymentAccessAsync(
        HttpContext http, OrderingDbContext db, GuestSessionTokens tokens, ShopTime clock, JwtTokenService jwt, CancellationToken ct)
    {
        var session = await SessionAsync(http, db, tokens, clock, mustBeOpen: true, ct);
        var (token, expires) = jwt.CreateAccessToken(session.Id, $"Table {session.TableCode}", AicosRoles.Guest, session.BranchId, session.TenantId);
        return new QrPaymentAccessResponse(token, expires, session.BranchId);
    }

    // ------------------------------------------------------------------ staff
    private static async Task<List<TableSessionSummary>> ListSessionsAsync(OrderingDbContext db, ICurrentUser user, Guid? branchId, CancellationToken ct)
    {
        var tenantId = TenantScope.Require(user);
        var branch = BranchAccess.ResolveFilter(user, branchId);
        var query = db.TableSessions.AsNoTracking().Where(s => s.TenantId == tenantId && s.Status == TableSessionStatus.Open);
        if (branch is not null)
        {
            query = query.Where(s => s.BranchId == branch);
        }

        var sessions = await query.OrderBy(s => s.TableCode).ToListAsync(ct);
        var ids = sessions.Select(s => s.Id).ToList();
        var orders = await db.Orders.AsNoTracking().Include(o => o.Items).Where(o => o.TableSessionId != null && ids.Contains(o.TableSessionId.Value)).ToListAsync(ct);
        return sessions.Select(s =>
        {
            var mine = orders.Where(o => o.TableSessionId == s.Id && !o.IsVoided).ToList();
            return new TableSessionSummary(s.Id, s.BranchId, s.TableId, s.TableCode, s.Status, s.OpenedAtUtc, s.LastActivityAtUtc, mine.Count, mine.Where(o => !o.IsPaid).Sum(o => o.FinalAmount), s.BillRequestedAtUtc, s.BillRequestedMethod);
        }).ToList();
    }

    private static async Task<TableSession> StaffSessionAsync(OrderingDbContext db, ICurrentUser user, Guid id, CancellationToken ct)
    {
        var tenantId = TenantScope.Require(user);
        var session = await db.TableSessions.FirstOrDefaultAsync(s => s.Id == id && s.TenantId == tenantId, ct) ?? throw new NotFoundException("Table session", id);
        BranchAccess.EnsureCanAccess(user, session.BranchId);
        return session;
    }

    private static async Task<QrBillResponse> StaffBillAsync(Guid id, OrderingDbContext db, ICurrentUser user, CancellationToken ct) =>
        await BuildBillAsync(db, await StaffSessionAsync(db, user, id, ct), ct);

    /// <summary>Ends the sitting only when no non-void round is unpaid. Never abandons an outstanding bill.</summary>
    private static async Task<IResult> CloseSessionAsync(Guid id, OrderingDbContext db, ICurrentUser user, ShopTime clock, OrderEventPublisher events, CancellationToken ct)
    {
        var session = await StaffSessionAsync(db, user, id, ct);
        if (!session.IsOpen) return Results.NoContent();
        if (await TableSessions.HasUnsettledOrdersAsync(db, session.Id, ct))
            throw new ConflictException("Settle all rounds before closing the table session.", "qr.session_unsettled");
        session.Close(TableSessionCloseReason.Staff, clock.UtcNow);
        await db.SaveChangesAsync(ct);
        await events.SessionClosedAsync(session, ct);
        return Results.NoContent();
    }
}

/// <summary>When a table session ends by itself.</summary>
public static class TableSessions
{
    public static Task<bool> HasUnsettledOrdersAsync(OrderingDbContext db, Guid sessionId, CancellationToken ct) =>
        db.Orders.AsNoTracking().AnyAsync(o => o.TableSessionId == sessionId
            && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.NoShow && o.Status != OrderStatus.Unfulfillable
            && o.PaymentStatus != PaymentStatus.Paid && o.PaymentStatus != PaymentStatus.Refunded, ct);

    /// <summary>Closes the session once it has orders and every order that was not cancelled is paid (all rounds settled).</summary>
    public static async Task CloseIfSettledAsync(OrderingDbContext db, Guid? sessionId, DateTime now, OrderEventPublisher events, CancellationToken ct)
    {
        if (sessionId is null)
        {
            return;
        }

        var session = await db.TableSessions.FirstOrDefaultAsync(s => s.Id == sessionId && s.Status == TableSessionStatus.Open, ct);
        if (session is null)
        {
            return;
        }

        var states = await db.Orders.AsNoTracking().Where(o => o.TableSessionId == sessionId && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.NoShow && o.Status != OrderStatus.Unfulfillable)
            .Select(o => o.PaymentStatus).ToListAsync(ct);
        if (states.Count == 0 || states.Any(p => p is not (PaymentStatus.Paid or PaymentStatus.Refunded)))
        {
            return;
        }

        session.Close(TableSessionCloseReason.Paid, now);
        await db.SaveChangesAsync(ct);
        await events.SessionClosedAsync(session, ct);
    }
}

/// <summary>Closes table sessions nobody used for <c>Ordering:Qr:SessionIdleHours</c> (default 6) hours.</summary>
public sealed class TableSessionExpiryWorker(IServiceScopeFactory scopes, TimeProvider clock, IConfiguration configuration, ILogger<TableSessionExpiryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var hours = int.TryParse(configuration["Ordering:Qr:SessionIdleHours"], out var h) && h is > 0 and <= 72 ? h : 6;
                await CloseIdleAsync(scope.ServiceProvider.GetRequiredService<OrderingDbContext>(), clock.GetUtcNow().UtcDateTime, TimeSpan.FromHours(hours), stoppingToken, scope.ServiceProvider.GetRequiredService<OrderEventPublisher>());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Closing idle table sessions failed; it is tried again in a few minutes.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public static async Task<int> CloseIdleAsync(OrderingDbContext db, DateTime now, TimeSpan idle, CancellationToken ct, OrderEventPublisher? events = null)
    {
        var limit = now - idle;
        var idleSessions = await db.TableSessions.Where(s => s.Status == TableSessionStatus.Open && s.LastActivityAtUtc < limit
            && !db.Orders.Any(o => o.TableSessionId == s.Id && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.NoShow && o.Status != OrderStatus.Unfulfillable
                && o.PaymentStatus != PaymentStatus.Paid && o.PaymentStatus != PaymentStatus.Refunded)).Take(200).ToListAsync(ct);
        foreach (var session in idleSessions)
        {
            session.Close(TableSessionCloseReason.Expired, now);
        }

        await db.SaveChangesAsync(ct);
        foreach (var session in idleSessions.Where(_ => events is not null))
        {
            await events!.SessionClosedAsync(session, ct);
        }

        return idleSessions.Count;
    }
}
