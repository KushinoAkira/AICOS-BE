using AICOS.BuildingBlocks.Messaging;
using AICOS.Contracts;

namespace AICOS.Ordering.Api.Services;

/// <summary>
/// EventBus giả lập chạy trong RAM khi máy dev chưa bật Redis (Port 6379).
/// Khi có Redis thật, chỉ cần gỡ đăng ký FakeEventBus trong Program.cs.
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
        _logger.LogInformation("📢 [FakeEventBus] Đã phát tán sự kiện {EventType} (EventId: {EventId}) lên EventBus (In-Memory Mock).",
            typeof(TEvent).Name, @event.EventId);

        return Task.CompletedTask;
    }
}
