using AICOS.KitchenOperations.Api.Features;
using AICOS.KitchenOperations.Api.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace AICOS.KitchenOperations.Api.Services;

public interface IKitchenNotifier
{
    Task NotifyNewTicketAsync(Guid branchId, KitchenTicketDto ticket, CancellationToken ct = default);
    Task NotifyStatusChangedAsync(Guid branchId, Guid ticketId, string newStatus, CancellationToken ct = default);
    Task NotifyOrderReadyForPickupAsync(Guid branchId, OrderReadyNotificationDto notification, CancellationToken ct = default);
    Task NotifyStickersPrintedAsync(Guid branchId, IReadOnlyList<CupStickerDto> stickers, CancellationToken ct = default);
}

public class KitchenSignalRNotifier : IKitchenNotifier
{
    private readonly IHubContext<KitchenHub> _hubContext;
    private readonly ILogger<KitchenSignalRNotifier> _logger;

    public KitchenSignalRNotifier(IHubContext<KitchenHub> hubContext, ILogger<KitchenSignalRNotifier> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task NotifyNewTicketAsync(Guid branchId, KitchenTicketDto ticket, CancellationToken ct = default)
    {
        var groupName = $"kitchen-{branchId}";
        await _hubContext.Clients.Group(groupName).SendAsync("ReceiveKitchenTicket", ticket, ct);
        _logger.LogInformation("SignalR: Đã đẩy đơn mới {TicketId} ({OrderCode}) tới nhóm {GroupName}",
            ticket.Id, ticket.OrderCode, groupName);
    }

    public async Task NotifyStatusChangedAsync(Guid branchId, Guid ticketId, string newStatus, CancellationToken ct = default)
    {
        var groupName = $"kitchen-{branchId}";
        await _hubContext.Clients.Group(groupName).SendAsync("TicketStatusChanged", new { TicketId = ticketId, Status = newStatus }, ct);
        _logger.LogInformation("SignalR: Đã thông báo trạng thái {Status} cho đơn {TicketId} tới nhóm {GroupName}",
            newStatus, ticketId, groupName);
    }

    public async Task NotifyOrderReadyForPickupAsync(Guid branchId, OrderReadyNotificationDto notification, CancellationToken ct = default)
    {
        // Gửi tới cả quầy POS (Thu ngân/Server - UC_POS_07) và màn hình KDS
        var posGroup = $"pos-{branchId}";
        var kitchenGroup = $"kitchen-{branchId}";

        await _hubContext.Clients.Group(posGroup).SendAsync("OrderReadyForPickup", notification, ct);
        await _hubContext.Clients.Group(kitchenGroup).SendAsync("OrderReadyForPickup", notification, ct);

        _logger.LogInformation("SignalR: Đã gửi thông báo Real-time Order Ready {OrderCode} tới quầy POS ({PosGroup})",
            notification.OrderCode, posGroup);
    }

    public async Task NotifyStickersPrintedAsync(Guid branchId, IReadOnlyList<CupStickerDto> stickers, CancellationToken ct = default)
    {
        var groupName = $"kitchen-{branchId}";
        await _hubContext.Clients.Group(groupName).SendAsync("CupStickersPrinted", stickers, ct);
        _logger.LogInformation("SignalR: Đã phát {Count} tem in ly nước tới {GroupName}", stickers.Count, groupName);
    }
}
