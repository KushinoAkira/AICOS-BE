using AICOS.KitchenOperations.Api.Features;
using AICOS.KitchenOperations.Api.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace AICOS.KitchenOperations.Api.Services;

/// <summary>Pushes kitchen changes to the screens of one branch of one brand (NFR-22).</summary>
public interface IKitchenNotifier
{
    Task NotifyNewTicketAsync(Guid tenantId, Guid branchId, KitchenTicketDto ticket, CancellationToken ct = default);

    Task NotifyStatusChangedAsync(Guid tenantId, Guid branchId, Guid ticketId, string newStatus, CancellationToken ct = default);

    Task NotifyOrderReadyForPickupAsync(Guid tenantId, Guid branchId, OrderReadyNotificationDto notification, CancellationToken ct = default);

    Task NotifyStickersPrintedAsync(Guid tenantId, Guid branchId, IReadOnlyList<CupStickerDto> stickers, CancellationToken ct = default);
}

public sealed class KitchenSignalRNotifier(IHubContext<KitchenHub> hub) : IKitchenNotifier
{
    public Task NotifyNewTicketAsync(Guid tenantId, Guid branchId, KitchenTicketDto ticket, CancellationToken ct = default) =>
        hub.Clients.Group(KitchenHub.KitchenGroup(tenantId, branchId)).SendAsync("ReceiveKitchenTicket", ticket, ct);

    public Task NotifyStatusChangedAsync(Guid tenantId, Guid branchId, Guid ticketId, string newStatus, CancellationToken ct = default) =>
        Task.WhenAll(
            hub.Clients.Group(KitchenHub.KitchenGroup(tenantId, branchId)).SendAsync("TicketStatusChanged", new { TicketId = ticketId, Status = newStatus }, ct),
            hub.Clients.Group(KitchenHub.PosGroup(tenantId, branchId)).SendAsync("TicketStatusChanged", new { TicketId = ticketId, Status = newStatus }, ct));

    /// <summary>To the cashier screens (SCRUM-58) and to the bar screens.</summary>
    public Task NotifyOrderReadyForPickupAsync(Guid tenantId, Guid branchId, OrderReadyNotificationDto notification, CancellationToken ct = default) =>
        Task.WhenAll(
            hub.Clients.Group(KitchenHub.PosGroup(tenantId, branchId)).SendAsync("OrderReadyForPickup", notification, ct),
            hub.Clients.Group(KitchenHub.KitchenGroup(tenantId, branchId)).SendAsync("OrderReadyForPickup", notification, ct));

    public Task NotifyStickersPrintedAsync(Guid tenantId, Guid branchId, IReadOnlyList<CupStickerDto> stickers, CancellationToken ct = default) =>
        hub.Clients.Group(KitchenHub.KitchenGroup(tenantId, branchId)).SendAsync("CupStickersPrinted", stickers, ct);
}
