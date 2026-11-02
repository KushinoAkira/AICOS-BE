using System.Globalization;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Persistence;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;
using AICOS.CustomerEngagement.Api.Domain;
using AICOS.CustomerEngagement.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.CustomerEngagement.Api.Features.Notifications;

public sealed record StaffNotificationResponse(Guid Id, string Kind, string Title, string Body, DateTime CreatedAtUtc, bool IsRead);

/// <summary>
/// The notifications of the people working in a brand. Routes: /api/v1/customers/staff/notifications. A person sees what was sent to their role,
/// in their brand, and for a role tied to a branch in their branch. They are created from the events of the other services.
/// </summary>
public static class StaffNotificationEndpoints
{
    public static void MapStaffNotificationEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/staff/notifications").WithTags("Staff notifications").RequireAuthorization(b => b.RequireRole(
            AicosRoles.SuperAdmin, AicosRoles.BrandAdmin, AicosRoles.BranchManager, AicosRoles.Cashier, AicosRoles.Barista));

        group.MapGet("/", ListAsync);
        group.MapGet("/unread-count", UnreadCountAsync);
        group.MapPost("/read-all", ReadAllAsync);
        group.MapPost("/{id:guid}/read", ReadAsync);
    }

    private static async Task<PagedResponse<StaffNotificationResponse>> ListAsync(
        CustomerEngagementDbContext db, ICurrentUser user, bool? unreadOnly, int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var userId = UserId(user);
        var query = Visible(db, user);
        if (unreadOnly == true)
        {
            query = query.Where(n => !db.StaffNotificationReads.Any(r => r.NotificationId == n.Id && r.UserId == userId));
        }

        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(n => n.CreatedAtUtc).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(n => new StaffNotificationResponse(
                n.Id, n.Kind, n.Title, n.Body, n.CreatedAtUtc, db.StaffNotificationReads.Any(r => r.NotificationId == n.Id && r.UserId == userId)))
            .ToListAsync(ct);
        return new PagedResponse<StaffNotificationResponse>(items, page, pageSize, total);
    }

    private static async Task<UnreadCountResponse> UnreadCountAsync(CustomerEngagementDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var userId = UserId(user);
        return new UnreadCountResponse(await Visible(db, user).CountAsync(n => !db.StaffNotificationReads.Any(r => r.NotificationId == n.Id && r.UserId == userId), ct));
    }

    private static async Task<IResult> ReadAsync(Guid id, CustomerEngagementDbContext db, ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        if (!await Visible(db, user).AnyAsync(n => n.Id == id, ct))
        {
            throw new NotFoundException("Notification", id);
        }

        await MarkAsync(db, UserId(user), [id], clock, ct);
        return Results.NoContent();
    }

    private static async Task<UnreadCountResponse> ReadAllAsync(CustomerEngagementDbContext db, ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        var ids = await Visible(db, user).Select(n => n.Id).ToListAsync(ct);
        await MarkAsync(db, UserId(user), ids, clock, ct);
        return new UnreadCountResponse(0);
    }

    private static async Task MarkAsync(CustomerEngagementDbContext db, Guid userId, List<Guid> ids, TimeProvider clock, CancellationToken ct)
    {
        var read = await db.StaffNotificationReads.Where(r => r.UserId == userId && ids.Contains(r.NotificationId)).Select(r => r.NotificationId).ToListAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        db.StaffNotificationReads.AddRange(ids.Except(read).Select(id => new StaffNotificationRead(id, userId, now)));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // The same person marked it read on another screen at the same moment: nothing is lost.
            db.ChangeTracker.Clear();
        }
    }

    private static IQueryable<StaffNotification> Visible(CustomerEngagementDbContext db, ICurrentUser user)
    {
        var role = user.Role;
        var tenantId = user.TenantId;
        var query = db.StaffNotifications.AsNoTracking().Where(n => n.Audience == role && n.TenantId == tenantId);
        if (role is AicosRoles.BranchManager or AicosRoles.Cashier or AicosRoles.Barista)
        {
            var branchId = user.BranchId;
            query = query.Where(n => n.BranchId == null || n.BranchId == branchId);
        }

        return query;
    }

    private static Guid UserId(ICurrentUser user) => user.UserId ?? throw new UnauthorizedException("Not signed in.");
}

/// <summary>Turns what happens in the other services into notifications for the people who must act on it. Safe to repeat: one per role and key.</summary>
public sealed class StaffNotificationHandler(CustomerEngagementDbContext db, TimeProvider clock, ILogger<StaffNotificationHandler> logger) :
    IIntegrationEventHandler<OrderCreatedIntegrationEvent>,
    IIntegrationEventHandler<OrderPaidIntegrationEvent>,
    IIntegrationEventHandler<KitchenOrderReadyIntegrationEvent>,
    IIntegrationEventHandler<KitchenOrderRemakeIntegrationEvent>,
    IIntegrationEventHandler<TableBillRequestedIntegrationEvent>,
    IIntegrationEventHandler<PaymentSucceededEvent>,
    IIntegrationEventHandler<RefundRequestedEvent>,
    IIntegrationEventHandler<PaymentRefundFailedEvent>,
    IIntegrationEventHandler<CashClosingSubmittedEvent>,
    IIntegrationEventHandler<ReplacementShiftOpenedEvent>,
    IIntegrationEventHandler<ReplacementRegisteredEvent>,
    IIntegrationEventHandler<SubscriptionRenewalDueEvent>,
    IIntegrationEventHandler<SubscriptionOverdueEvent>,
    IIntegrationEventHandler<BrandRegisteredEvent>
{
    private static readonly CultureInfo Vietnamese = CultureInfo.GetCultureInfo("vi-VN");

    private static string Money(decimal amount) => amount.ToString("N0", Vietnamese) + "đ";

    private static string Where(string? tableNumber) => string.IsNullOrWhiteSpace(tableNumber) ? "Mang đi" : $"Bàn {tableNumber}";

    /// <summary>A dine-in order goes to the bar at once, any other order when it is paid (BR-43). The key is the same as for the payment, so the bar hears once.</summary>
    public async Task HandleAsync(OrderCreatedIntegrationEvent e, CancellationToken ct)
    {
        if (e.IsPaid || e.OrderType == "DineIn")
        {
            await AddAsync(e.TenantId, e.BranchId, AicosRoles.Barista, "NewOrder", $"order:{e.OrderId}", $"Đơn mới {e.OrderCode}",
                $"{Where(e.TableNumber)}: {e.Items.Sum(i => i.Quantity)} món cần pha.", ct);
        }

        // Nobody at the counter took this order: the guests sent it from their table.
        if (e.CashierId is null && !string.IsNullOrWhiteSpace(e.TableNumber))
        {
            await AddAsync(e.TenantId, e.BranchId, AicosRoles.Cashier, "TableOrder", $"order:{e.OrderId}", $"Bàn {e.TableNumber} vừa gọi món",
                $"Đơn {e.OrderCode}, tổng {Money(e.TotalAmount)}.", ct);
        }
    }

    public Task HandleAsync(OrderPaidIntegrationEvent e, CancellationToken ct) =>
        AddAsync(e.TenantId, e.BranchId, AicosRoles.Barista, "NewOrder", $"order:{e.OrderId}", $"Đơn mới {e.OrderCode}", "Đơn đã thanh toán, cần pha.", ct);

    public Task HandleAsync(KitchenOrderReadyIntegrationEvent e, CancellationToken ct) =>
        AddAsync(e.TenantId, e.BranchId, AicosRoles.Cashier, "OrderReady", $"ready:{e.TicketId}:{e.ReadyAtUtc.Ticks}", $"Đơn {e.OrderCode} đã xong",
            $"{Where(e.TableNumber)}: sẵn sàng giao cho khách.", ct);

    public Task HandleAsync(KitchenOrderRemakeIntegrationEvent e, CancellationToken ct) =>
        AddAsync(e.TenantId, e.BranchId, AicosRoles.Barista, "Remake", $"remake:{e.TicketId}:{e.RemakeCount}", "Có đơn cần làm lại", $"Lý do: {e.Reason}", ct);

    public Task HandleAsync(TableBillRequestedIntegrationEvent e, CancellationToken ct) =>
        AddAsync(e.TenantId, e.BranchId, AicosRoles.Cashier, "BillRequested", $"bill:{e.EventId}", $"Bàn {e.TableCode} gọi tính tiền",
            e.Method == "Cash" ? "Khách trả tiền mặt." : "Khách muốn chuyển khoản.", ct);

    public async Task HandleAsync(PaymentSucceededEvent e, CancellationToken ct)
    {
        if (e.Method != "Cash")
        {
            await AddAsync(e.TenantId, e.BranchId, AicosRoles.Cashier, "OnlinePaid", $"paid:{e.PaymentId}", "Khách đã thanh toán online", $"Đã nhận {Money(e.Amount)} qua {e.Method}.", ct);
        }
    }

    public async Task HandleAsync(RefundRequestedEvent e, CancellationToken ct)
    {
        var body = $"{Money(e.Amount)}. Lý do: {e.Reason}";
        await AddAsync(e.TenantId, e.BranchId, AicosRoles.BranchManager, "RefundRequested", $"refund:{e.RefundId}", "Có yêu cầu hoàn tiền chờ duyệt", body, ct);
        await AddAsync(e.TenantId, e.BranchId, AicosRoles.BrandAdmin, "RefundRequested", $"refund:{e.RefundId}", "Có yêu cầu hoàn tiền chờ duyệt", body, ct);
    }

    public async Task HandleAsync(PaymentRefundFailedEvent e, CancellationToken ct)
    {
        await AddAsync(e.TenantId, e.BranchId, AicosRoles.BranchManager, "RefundFailed", $"refund-failed:{e.RefundId}", "Hoàn tiền không thành công", e.Reason, ct);
        await AddAsync(e.TenantId, e.BranchId, AicosRoles.BrandAdmin, "RefundFailed", $"refund-failed:{e.RefundId}", "Hoàn tiền không thành công", e.Reason, ct);
    }

    public Task HandleAsync(CashClosingSubmittedEvent e, CancellationToken ct)
    {
        var difference = e.CountedAmount - e.ExpectedAmount;
        return AddAsync(e.TenantId, e.BranchId, AicosRoles.BranchManager, "CashClosing", $"closing:{e.ClosingId}", $"{e.EmployeeName} đã gửi chốt két",
            difference == 0 ? $"Đếm được {Money(e.CountedAmount)}, khớp với hệ thống." : $"Đếm được {Money(e.CountedAmount)}, lệch {Money(Math.Abs(difference))} so với hệ thống.", ct);
    }

    public async Task HandleAsync(ReplacementShiftOpenedEvent e, CancellationToken ct)
    {
        var body = $"Ca {e.StartTime:HH\\:mm}-{e.EndTime:HH\\:mm} ngày {e.WorkDate:dd/MM/yyyy} đang cần người.";
        await AddAsync(e.TenantId, e.BranchId, AicosRoles.Cashier, "ReplacementOpened", $"replacement:{e.ReplacementId}", "Có ca cần người làm thay", body, ct);
        await AddAsync(e.TenantId, e.BranchId, AicosRoles.Barista, "ReplacementOpened", $"replacement:{e.ReplacementId}", "Có ca cần người làm thay", body, ct);
    }

    public Task HandleAsync(ReplacementRegisteredEvent e, CancellationToken ct) =>
        AddAsync(e.TenantId, e.BranchId, AicosRoles.BranchManager, "ReplacementRegistered", $"replacement:{e.ReplacementId}:{e.EmployeeId}", $"{e.EmployeeName} đăng ký làm thay",
            $"Ca ngày {e.WorkDate:dd/MM/yyyy} đang chờ bạn duyệt.", ct);

    public Task HandleAsync(SubscriptionRenewalDueEvent e, CancellationToken ct) =>
        AddAsync(e.TenantId, null, AicosRoles.BrandAdmin, "RenewalDue", $"renewal:{e.PeriodEndUtc:yyyyMMdd}:{e.DaysLeft}", "Sắp đến hạn gia hạn gói",
            $"Gói {e.PlanName} hết hạn sau {e.DaysLeft} ngày.", ct);

    public async Task HandleAsync(SubscriptionOverdueEvent e, CancellationToken ct)
    {
        await AddAsync(e.TenantId, null, AicosRoles.BrandAdmin, "Overdue", $"overdue:{e.PeriodEndUtc:yyyyMMdd}", "Gói dịch vụ đã quá hạn", $"Gói {e.PlanName} đã quá hạn {e.DaysOverdue} ngày.", ct);
        if (e.GracePeriodOver)
        {
            await AddAsync(null, null, AicosRoles.SuperAdmin, "Overdue", $"overdue:{e.TenantId}:{e.PeriodEndUtc:yyyyMMdd}", $"{e.TenantName} đã hết thời gian gia hạn",
                $"Gói {e.PlanName} quá hạn {e.DaysOverdue} ngày, có thể tạm ngưng thương hiệu.", ct);
        }
    }

    public Task HandleAsync(BrandRegisteredEvent e, CancellationToken ct) =>
        AddAsync(null, null, AicosRoles.SuperAdmin, "BrandRegistered", $"brand:{e.TenantId}", $"{e.BrandName} đăng ký thương hiệu mới", $"Mã {e.Code}, chủ quán {e.OwnerEmail}. Đang chờ bạn duyệt.", ct);

    private async Task AddAsync(Guid? tenantId, Guid? branchId, string audience, string kind, string dedupeKey, string title, string body, CancellationToken ct)
    {
        if (await db.StaffNotifications.AnyAsync(n => n.TenantId == tenantId && n.Audience == audience && n.DedupeKey == dedupeKey, ct))
        {
            return;
        }

        var notification = new StaffNotification(
            tenantId, branchId, audience, kind, dedupeKey, Cut(title, StaffNotificationLimits.Title), Cut(body, StaffNotificationLimits.Body), clock.GetUtcNow().UtcDateTime);
        db.StaffNotifications.Add(notification);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            db.Entry(notification).State = EntityState.Detached;
            logger.LogInformation(ex, "Staff notification {Key} was created by a concurrent delivery of the same event.", dedupeKey);
        }
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max];
}

public static class StaffNotificationLimits
{
    public const int Title = 150;
    public const int Body = 500;
}
