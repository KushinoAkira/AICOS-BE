using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;
using AICOS.Ordering.Api.Domain;
using AICOS.Ordering.Api.Domain.Entities;
using AICOS.Ordering.Api.Features.Qr;
using AICOS.Ordering.Api.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace AICOS.Ordering.Api.Features.Orders;

/// <summary>What a customer's screen receives when an order changes (the <c>OrderChanged</c> message of <see cref="OrderTrackingHub"/>).</summary>
public sealed record OrderChangedMessage(
    Guid OrderId,
    string OrderCode,
    string Kind,
    string Status,
    string PaymentStatus,
    string RefundStatus,
    int RemakeCount,
    DateTime? ReadyAtUtc,
    decimal FinalAmount,
    Guid? TableSessionId,
    string Title,
    string Message,
    DateTime AtUtc);

/// <summary>The table session ended (everything paid, staff closed it, or it expired): the guest screen should stop ordering.</summary>
public sealed record SessionClosedMessage(Guid SessionId, string Reason, DateTime AtUtc);

/// <summary>
/// Real-time channel for customers (replaces polling), at <c>/hubs/orders</c>. A guest at a table connects with the session token from the QR scan
/// as <c>?sessionToken=</c> and follows every order of the table; a signed-in member connects with their token (<c>?access_token=</c>) and follows
/// all their own orders. The server sends <c>OrderChanged</c> and <c>SessionClosed</c>; the client sends nothing.
/// </summary>
[AllowAnonymous]
public sealed class OrderTrackingHub(GuestSessionTokens tokens, IServiceScopeFactory scopes) : Hub
{
    public static string QrGroup(Guid sessionId) => $"qr-{sessionId:N}";

    public static string MemberGroup(Guid tenantId, Guid userId) => $"member-{tenantId:N}-{userId:N}";

    public override async Task OnConnectedAsync()
    {
        var joined = false;
        var http = Context.GetHttpContext();
        if (tokens.TryRead(http?.Request.Query["sessionToken"].ToString(), out var sessionId))
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            if (await db.TableSessions.AsNoTracking().AnyAsync(s => s.Id == sessionId))
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, QrGroup(sessionId));
                joined = true;
            }
        }

        var user = Context.User;
        if (user?.Identity?.IsAuthenticated == true && user.IsInRole(AicosRoles.Customer)
            && Guid.TryParse(user.FindFirst(AicosClaimTypes.TenantId)?.Value, out var tenantId) && Guid.TryParse(user.FindFirst(AicosClaimTypes.UserId)?.Value, out var userId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, MemberGroup(tenantId, userId));
            joined = true;
        }

        if (!joined)
        {
            throw new HubException("Scan the table QR code (sessionToken) or sign in as a customer to follow your orders.");
        }

        await base.OnConnectedAsync();
    }
}

/// <summary>Tells the screens of the people concerned that an order or a table session changed. A failure to push is logged and never fails the change.</summary>
public interface IOrderNotifier
{
    Task OrderChangedAsync(Order order, string kind, CancellationToken ct);

    Task SessionClosedAsync(TableSession session, CancellationToken ct);
}

public sealed class SignalROrderNotifier(IHubContext<OrderTrackingHub> hub, ILogger<SignalROrderNotifier> logger) : IOrderNotifier
{
    public async Task OrderChangedAsync(Order order, string kind, CancellationToken ct)
    {
        try
        {
            var (title, body) = OrderStatusTexts.For(kind, order.OrderCode, order.TableNumber);
            var message = new OrderChangedMessage(
                order.Id, order.OrderCode, kind, order.Status.ToString(), order.PaymentStatus.ToString(), order.EffectiveRefundStatus.ToString(), order.RemakeCount,
                order.ReadyAtUtc, order.FinalAmount, order.TableSessionId, title, body, DateTime.UtcNow);
            var tasks = new List<Task>();
            if (order.TableSessionId is { } sessionId)
            {
                tasks.Add(hub.Clients.Group(OrderTrackingHub.QrGroup(sessionId)).SendAsync("OrderChanged", message, ct));
            }

            if (order.CustomerId is { } customerId)
            {
                tasks.Add(hub.Clients.Group(OrderTrackingHub.MemberGroup(order.TenantId, customerId)).SendAsync("OrderChanged", message, ct));
            }

            await Task.WhenAll(tasks);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not push the change of order {OrderCode} to the customer screens.", order.OrderCode);
        }
    }

    public async Task SessionClosedAsync(TableSession session, CancellationToken ct)
    {
        try
        {
            await hub.Clients.Group(OrderTrackingHub.QrGroup(session.Id))
                .SendAsync("SessionClosed", new SessionClosedMessage(session.Id, (session.CloseReason ?? TableSessionCloseReason.Staff).ToString(), DateTime.UtcNow), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not push the end of table session {SessionId}.", session.Id);
        }
    }
}
