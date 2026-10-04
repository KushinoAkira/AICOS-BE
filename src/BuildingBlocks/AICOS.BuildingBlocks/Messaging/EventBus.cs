using System.Text.Json;
using AICOS.Contracts;

namespace AICOS.BuildingBlocks.Messaging;

/// <summary>Publishes integration events to other services (fire-and-forget over Redis Pub/Sub).</summary>
public interface IEventBus
{
    Task PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
        where TEvent : IntegrationEvent;
}

/// <summary>
/// Handles one integration event type. Register with
/// <c>services.AddIntegrationEventHandler&lt;TEvent, THandler&gt;()</c>. Handlers are resolved from a fresh DI scope per message.
/// </summary>
public interface IIntegrationEventHandler<in TEvent>
    where TEvent : IntegrationEvent
{
    Task HandleAsync(TEvent @event, CancellationToken cancellationToken);
}

internal static class EventBusConventions
{
    private const string ChannelPrefix = "aicos:events:";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string ChannelFor<TEvent>() => ChannelPrefix + typeof(TEvent).Name;
}
