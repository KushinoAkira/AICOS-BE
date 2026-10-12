namespace AICOS.Contracts.Events;

/// <summary>
/// Published by Identity Service when a Customer account is created (self-registration UC-33 or by an Admin).
/// Consumed by Customer Engagement to create the customer profile / loyalty wallet linked to <see cref="UserId"/>.
/// </summary>
public sealed record CustomerRegisteredEvent(Guid UserId, string FullName, string Email, string? Phone) : IntegrationEvent;
