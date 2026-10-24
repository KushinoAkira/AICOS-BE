using AICOS.BuildingBlocks.Messaging;
using AICOS.Contracts;

namespace AICOS.KitchenOperations.Api.Services;

/// <summary>
/// EventBus giả lập chạy trong RAM khi máy dev chưa bật Redis (Port 6379).
/// </summary>
public class FakeEventBus : IEventBus
{
    private readonly ILogger<FakeEventBus> _logger;

    public FakeEventBus(ILogger<FakeEventBus> logger)
    {
        _logger = logger;
    }

    public Task PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
        where TEvent : IntegrationEvent
    {
        _logger.LogInformation("📢 [Kitchen FakeEventBus] Đã phát tán sự kiện {EventType} (EventId: {EventId})",
            typeof(TEvent).Name, @event.EventId);

        return Task.CompletedTask;
    }
}
