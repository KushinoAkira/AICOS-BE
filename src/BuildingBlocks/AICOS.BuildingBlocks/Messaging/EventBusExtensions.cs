using System.Text.Json;
using AICOS.Contracts;

namespace AICOS.BuildingBlocks.Messaging;

public static class EventBusExtensions
{
    /// <summary>
    /// Adds the Redis Pub/Sub event bus (<see cref="IEventBus"/>), the background subscriber and a Redis readiness check.
    /// Reads <c>ConnectionStrings:Redis</c> (default <c>localhost:6379</c>).
    /// </summary>
    public static WebApplicationBuilder AddAicosEventBus(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<RedisConnectionProvider>();
        builder.Services.AddSingleton<IEventBus, RedisEventBus>();
        builder.Services.AddHostedService<RedisEventBusSubscriber>();

        builder.Services.AddHealthChecks()
            .AddRedis(builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379", name: "redis", tags: ["ready"]);

        return builder;
    }

    /// <summary>Subscribes <typeparamref name="THandler"/> to <typeparamref name="TEvent"/>. Call once per handler.</summary>
    public static IServiceCollection AddIntegrationEventHandler<TEvent, THandler>(this IServiceCollection services)
        where TEvent : IntegrationEvent
        where THandler : class, IIntegrationEventHandler<TEvent>
    {
        services.AddScoped<IIntegrationEventHandler<TEvent>, THandler>();

        // The dispatcher runs ALL registered handlers of TEvent; the subscriber de-duplicates by channel.
        services.AddSingleton(new EventSubscription(
            EventBusConventions.ChannelFor<TEvent>(),
            async (provider, payload, cancellationToken) =>
            {
                var @event = JsonSerializer.Deserialize<TEvent>(payload, EventBusConventions.Json);
                if (@event is null)
                {
                    return;
                }

                foreach (var handler in provider.GetServices<IIntegrationEventHandler<TEvent>>())
                {
                    await handler.HandleAsync(@event, cancellationToken);
                }
            }));

        return services;
    }
}
