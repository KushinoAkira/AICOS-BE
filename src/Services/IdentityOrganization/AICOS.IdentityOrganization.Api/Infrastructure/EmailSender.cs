namespace AICOS.IdentityOrganization.Api.Infrastructure;

public interface IEmailSender
{
    Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken);
}

/// <summary>
/// DEV implementation: writes the email to the service log instead of sending it (the reset code shows up in the console).
/// Replace with an SMTP / SendGrid implementation before going live.
/// </summary>
internal sealed class LoggingEmailSender(ILogger<LoggingEmailSender> logger) : IEmailSender
{
    public Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken)
    {
        logger.LogWarning("[DEV EMAIL - not really sent] To: {To} | Subject: {Subject} | {Body}", to, subject, body);
        return Task.CompletedTask;
    }
}
