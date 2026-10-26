using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace AICOS.IdentityOrganization.Api.Infrastructure;

/// <summary>
/// Mail settings, section <c>Email</c> (environment variables <c>Email__Smtp__Host</c>, ...). Nothing here has a secret default: with <c>Smtp:Host</c>
/// empty the service only logs mails (see <see cref="LoggingEmailSender"/>); fill the values in <c>.env</c> to send real ones.
/// </summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>Address shown as the sender, for example <c>no-reply@yourbrand.vn</c>. Required with a host.</summary>
    public string FromAddress { get; set; } = string.Empty;

    public string FromName { get; set; } = "AICOS";

    /// <summary>Address of the web app, for the sign-in link in the mails (for example <c>https://app.yourbrand.vn</c>). Empty: no link.</summary>
    public string AppUrl { get; set; } = string.Empty;

    public SmtpOptions Smtp { get; set; } = new();

    /// <summary>True when a mail server is set up well enough to send.</summary>
    public bool IsSmtpConfigured => !string.IsNullOrWhiteSpace(Smtp.Host) && !string.IsNullOrWhiteSpace(FromAddress);
}

public sealed class SmtpOptions
{
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 587;

    /// <summary>StartTls (port 587, default), Ssl (port 465) or None (a local test server only).</summary>
    public string Security { get; set; } = "StartTls";

    public string Username { get; set; } = string.Empty;

    /// <summary>Secret: set it through the environment, never in a file that is committed.</summary>
    public string Password { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 15;

    public SecureSocketOptions SocketOptions => Security.Trim().ToLowerInvariant() switch
    {
        "ssl" or "sslonconnect" => SecureSocketOptions.SslOnConnect,
        "none" => SecureSocketOptions.None,
        _ => SecureSocketOptions.StartTls,
    };
}

/// <summary>
/// Sends the mails through an SMTP server with MailKit: a multipart message with a text and an HTML part. One short connection per mail, with
/// a timeout, so a slow or broken mail server cannot hold a request for long. The password and the body (which may hold a reset code) are never logged.
/// </summary>
public sealed class SmtpEmailSender(IOptions<EmailOptions> options, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    public Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken) =>
        SendAsync(new EmailMessage(to, subject, body), cancellationToken);

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(settings.FromName, settings.FromAddress));
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;
        mime.Body = new BodyBuilder { TextBody = message.TextBody, HtmlBody = message.HtmlBody }.ToMessageBody();

        using var client = new SmtpClient { Timeout = Math.Clamp(settings.Smtp.TimeoutSeconds, 3, 60) * 1000 };
        try
        {
            await client.ConnectAsync(settings.Smtp.Host, settings.Smtp.Port, settings.Smtp.SocketOptions, cancellationToken);
            if (!string.IsNullOrEmpty(settings.Smtp.Username))
            {
                await client.AuthenticateAsync(settings.Smtp.Username, settings.Smtp.Password, cancellationToken);
            }

            await client.SendAsync(mime, cancellationToken);
            await client.DisconnectAsync(quit: true, cancellationToken);
            logger.LogInformation("Mail '{Subject}' sent to {To}.", message.Subject, LoggingEmailSender.Mask(message.To));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The exception text may name the account; log its type and the masked address only.
            logger.LogError("Mail '{Subject}' to {To} could not be sent ({Reason}).", message.Subject, LoggingEmailSender.Mask(message.To), ex.GetType().Name);
            throw;
        }
    }
}
