namespace AICOS.Contracts.Events;

/// <summary>
/// Published by Identity Service when a Customer account is created (self-registration UC-33 or by an Admin).
/// Consumed by Customer Engagement to create the customer profile / loyalty wallet linked to <see cref="UserId"/>.
/// </summary>
public sealed record CustomerRegisteredEvent(Guid UserId, string FullName, string Email, string? Phone, Guid TenantId) : IntegrationEvent;

/// <summary>
/// Published by Identity Service when an account owner edits their own name or phone (UC-03).
/// Consumed by Customer Engagement to keep the copy of the profile in sync (Customer accounts only).
/// </summary>
public sealed record UserProfileUpdatedEvent(Guid UserId, string FullName, string? Phone) : IntegrationEvent;

/// <summary>
/// Published by Identity Service when a customer deletes their personal data (UC-66, BR-70).
/// Consumed by Customer Engagement to anonymize the customer's profile at every brand; transaction records are kept.
/// </summary>
public sealed record CustomerDataErasedEvent(Guid UserId) : IntegrationEvent;
