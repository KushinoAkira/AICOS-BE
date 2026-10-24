using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Persistence;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;
using AICOS.CustomerEngagement.Api.Domain;
using AICOS.CustomerEngagement.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.CustomerEngagement.Api.Features.Notifications;

public sealed record NotificationResponse(Guid Id, string Kind, string Title, string Body, Guid OrderId, string OrderCode, DateTime CreatedAtUtc, DateTime? ReadAtUtc, bool IsRead)
{
    public static NotificationResponse From(CustomerNotification n) =>
        new(n.Id, n.Kind, n.Title, n.Body, n.OrderId, n.OrderCode, n.CreatedAtUtc, n.ReadAtUtc, n.ReadAtUtc is not null);
}

public sealed record UnreadCountResponse(int Unread);

/// <summary>
/// The notifications of a member about their orders. Routes: /api/v1/customers/me/notifications. Only the customer account itself sees them.
/// They are created from the order events; the live push to the open screen comes from the Order service (<c>/hubs/orders</c>), so a screen
/// shows the toast at once and reloads the list or the unread count from here.
/// </summary>
public static class NotificationEndpoints
{
    public static void MapNotificationEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/me/notifications").WithTags("Notifications").RequireAuthorization(b => b.RequireRole(AicosRoles.Customer));

        group.MapGet("/", ListAsync);
        group.MapGet("/unread-count", UnreadCountAsync);
        group.MapPost("/read-all", ReadAllAsync);
        group.MapPost("/{id:guid}/read", ReadAsync);
    }

    private static async Task<PagedResponse<NotificationResponse>> ListAsync(
        CustomerEngagementDbContext db, ICurrentUser user, bool? unreadOnly, int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        var (tenantId, userId) = Who(user);
        var query = db.Notifications.AsNoTracking().Where(n => n.TenantId == tenantId && n.UserId == userId);
        if (unreadOnly == true)
        {
            query = query.Where(n => n.ReadAtUtc == null);
        }

        return await query.OrderByDescending(n => n.CreatedAtUtc).ToPagedAsync(Math.Max(page, 1), Math.Clamp(pageSize, 1, 100), NotificationResponse.From, ct);
    }

    private static async Task<UnreadCountResponse> UnreadCountAsync(CustomerEngagementDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var (tenantId, userId) = Who(user);
        return new UnreadCountResponse(await db.Notifications.CountAsync(n => n.TenantId == tenantId && n.UserId == userId && n.ReadAtUtc == null, ct));
    }

    private static async Task<NotificationResponse> ReadAsync(Guid id, CustomerEngagementDbContext db, ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        var (tenantId, userId) = Who(user);
        var notification = await db.Notifications.FirstOrDefaultAsync(n => n.Id == id && n.TenantId == tenantId && n.UserId == userId, ct)
            ?? throw new NotFoundException("Notification", id);
        if (notification.MarkRead(clock.GetUtcNow().UtcDateTime))
        {
            await db.SaveChangesAsync(ct);
        }

        return NotificationResponse.From(notification);
    }

    private static async Task<UnreadCountResponse> ReadAllAsync(CustomerEngagementDbContext db, ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        var (tenantId, userId) = Who(user);
        var now = clock.GetUtcNow().UtcDateTime;
        var unread = await db.Notifications.Where(n => n.TenantId == tenantId && n.UserId == userId && n.ReadAtUtc == null).ToListAsync(ct);
        foreach (var n in unread)
        {
            n.MarkRead(now);
        }

        await db.SaveChangesAsync(ct);
        return new UnreadCountResponse(0);
    }

    private static (Guid TenantId, Guid UserId) Who(ICurrentUser user) =>
        (TenantScope.Require(user), user.UserId ?? throw new UnauthorizedException("Not signed in."));
}

/// <summary>Order -> Customer: something happened to an order of a member; keep it as a notification. Safe to repeat: one per order and kind.</summary>
public sealed class OrderStatusNotificationHandler(CustomerEngagementDbContext db, ILogger<OrderStatusNotificationHandler> logger)
    : IIntegrationEventHandler<OrderStatusChangedIntegrationEvent>
{
    public async Task HandleAsync(OrderStatusChangedIntegrationEvent @event, CancellationToken cancellationToken)
    {
        if (@event.CustomerId == Guid.Empty || await db.Notifications.AnyAsync(
                n => n.TenantId == @event.TenantId && n.UserId == @event.CustomerId && n.OrderId == @event.OrderId && n.DedupeKey == @event.DedupeKey, cancellationToken))
        {
            return;
        }

        var (title, body) = OrderStatusTexts.For(@event.Kind, @event.OrderCode, @event.TableNumber);
        db.Notifications.Add(new CustomerNotification(@event.TenantId, @event.CustomerId, @event.OrderId, @event.OrderCode, @event.Kind, @event.DedupeKey, title, body));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            logger.LogInformation(ex, "Notification {Kind} of order {OrderCode} was created by a concurrent delivery of the same event.", @event.Kind, @event.OrderCode);
        }
    }
}
