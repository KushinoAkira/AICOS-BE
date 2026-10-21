using Microsoft.AspNetCore.SignalR;

namespace AICOS.KitchenOperations.Api.Hubs;

/// <summary>
/// SignalR Hub cho Kitchen Display System (KDS) và quầy Thu ngân (POS).
/// Hỗ trợ WebSocket real-time (< 300ms theo NFR-28).
/// </summary>
public class KitchenHub : Hub
{
    private readonly ILogger<KitchenHub> _logger;

    public KitchenHub(ILogger<KitchenHub> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Màn hình Barista/KDS tham gia vào nhóm chi nhánh của mình
    /// </summary>
    public async Task JoinKitchenGroup(string branchId)
    {
        var groupName = $"kitchen-{branchId}";
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
        _logger.LogInformation("Client {ConnectionId} đã tham gia nhóm KDS: {GroupName}", Context.ConnectionId, groupName);
    }

    /// <summary>
    /// Màn hình Thu ngân/Phục vụ tham gia để nhận thông báo đơn sẵn sàng (UC_POS_07)
    /// </summary>
    public async Task JoinPosGroup(string branchId)
    {
        var groupName = $"pos-{branchId}";
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
        _logger.LogInformation("Client {ConnectionId} đã tham gia nhóm POS: {GroupName}", Context.ConnectionId, groupName);
    }

    public override Task OnConnectedAsync()
    {
        _logger.LogInformation("SignalR Client kết nối thành công: {ConnectionId}", Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("SignalR Client ngắt kết nối: {ConnectionId}", Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
