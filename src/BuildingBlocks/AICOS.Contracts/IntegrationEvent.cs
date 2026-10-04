namespace AICOS.Contracts;

/// <summary>
/// Base type for every event published on the Redis event bus.
/// Events are immutable facts ("something happened"), named in the past tense.
/// </summary>
public abstract record IntegrationEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();

    public DateTime OccurredOnUtc { get; init; } = DateTime.UtcNow;
}
