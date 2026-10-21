namespace AICOS.IdentityOrganization.Api.Infrastructure;

public interface IEmailSender
{
    Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken);
}

/// <summary>
/// Stand-in for a real mail provider: writes the email to the service log instead of sending it. Replace with an SMTP / SendGrid
/// implementation before going live. Only in the Development environment does the log carry the address and the body (the reset code
/// shows up in the console); everywhere else it records that a mail was requested, with a masked address and no body, so the log never
/// holds a code, a token or an email address (SCRUM-148).
/// </summary>
public sealed class LoggingEmailSender(ILogger<LoggingEmailSender> logger, IHostEnvironment environment) : IEmailSender
{
    public Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken)
    {
        if (environment.IsDevelopment())
        {
            logger.LogWarning("[DEV EMAIL - not really sent] To: {To} | Subject: {Subject} | {Body}", to, subject, body);
        }
        else
        {
            logger.LogWarning("Email requested but no mail provider is configured. To: {To} | Subject: {Subject}", Mask(to), subject);
        }

        return Task.CompletedTask;
    }

    /// <summary>"nguyen.van@example.com" becomes "n***@example.com".</summary>
    public static string Mask(string email)
    {
        var at = email.IndexOf('@');
        return at < 1 ? "***" : $"{email[0]}***{email[at..]}";
    }
}
