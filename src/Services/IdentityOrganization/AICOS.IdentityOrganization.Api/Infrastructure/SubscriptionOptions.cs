using System.ComponentModel.DataAnnotations;

namespace AICOS.IdentityOrganization.Api.Infrastructure;

/// <summary>Subscription timing rules. Section "Subscriptions" in appsettings. The defaults are assumptions to be confirmed (BR-67).</summary>
public sealed class SubscriptionOptions
{
    public const string SectionName = "Subscriptions";

    /// <summary>BR-67: days after the renewal date during which an unrenewed tenant keeps working before it counts as overdue.</summary>
    [Range(0, 90)]
    public int GracePeriodDays { get; set; } = 7;

    /// <summary>BR-10: how many days before the renewal date the brand is reminded.</summary>
    [Range(1, 60)]
    public int RenewalNoticeDays { get; set; } = 7;

    /// <summary>How often the reminder job runs. 0 turns the background job off (the tests and one-off runs call it directly).</summary>
    [Range(0, 10080)]
    public int ReminderIntervalMinutes { get; set; } = 360;
}
