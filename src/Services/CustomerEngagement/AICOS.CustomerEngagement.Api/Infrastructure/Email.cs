using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace AICOS.CustomerEngagement.Api.Infrastructure;

public sealed record EmailMessage(string To, string Subject, string TextBody, string? HtmlBody = null);

public interface IEmailSender
{
    Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken);

    Task SendAsync(EmailMessage message, CancellationToken cancellationToken) =>
        SendAsync(message.To, message.Subject, message.TextBody, cancellationToken);
}

public sealed class EmailOptions
{
    public const string SectionName = "Email";

    public string FromAddress { get; set; } = string.Empty;

    public string FromName { get; set; } = "AICOS";

    public string AppUrl { get; set; } = string.Empty;

    public SmtpOptions Smtp { get; set; } = new();

    public bool IsSmtpConfigured => !string.IsNullOrWhiteSpace(Smtp.Host) && !string.IsNullOrWhiteSpace(FromAddress);
}

public sealed class SmtpOptions
{
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 587;

    public string Security { get; set; } = "StartTls";

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 15;

    public SecureSocketOptions SocketOptions => Security.Trim().ToLowerInvariant() switch
    {
        "ssl" or "sslonconnect" => SecureSocketOptions.SslOnConnect,
        "none" => SecureSocketOptions.None,
        _ => SecureSocketOptions.StartTls,
    };
}

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

    public static string Mask(string email)
    {
        var at = email.IndexOf('@');
        return at < 1 ? "***" : $"{email[0]}***{email[at..]}";
    }
}

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
            logger.LogError("Mail '{Subject}' to {To} could not be sent ({Reason}).", message.Subject, LoggingEmailSender.Mask(message.To), ex.GetType().Name);
            throw;
        }
    }
}

public static class EmailServiceCollectionExtensions
{
    public static IServiceCollection AddAicosEmail(this IServiceCollection services, IConfiguration configuration, ILogger? startupLog = null)
    {
        var section = configuration.GetSection(EmailOptions.SectionName);
        services.Configure<EmailOptions>(section);
        var options = section.Get<EmailOptions>() ?? new EmailOptions();
        if (options.IsSmtpConfigured)
        {
            services.AddSingleton<IEmailSender, SmtpEmailSender>();
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(options.Smtp.Host))
            {
                startupLog?.LogWarning("Email:Smtp:Host is set but Email:FromAddress is empty: mails are only logged until the sender address is set.");
            }

            services.AddSingleton<IEmailSender, LoggingEmailSender>();
        }

        return services;
    }
}

public static class CrmEmailTemplates
{
    public static EmailMessage Birthday(string to, string name, string voucherCode, int percent, DateTime expires) =>
        new(to, "Chúc mừng sinh nhật từ quán!",
            $"Xin chào {name},\n\nChúc bạn sinh nhật vui vẻ! Quán gửi mã {voucherCode} giảm {percent}% (HSD {expires:dd/MM/yyyy}).\nHẹn gặp bạn sớm nhé!\n",
            $"<p>Xin chào <b>{System.Net.WebUtility.HtmlEncode(name)}</b>,</p><p>Chúc mừng sinh nhật! Mã <b>{voucherCode}</b> giảm {percent}% (đến {expires:dd/MM/yyyy}).</p>");

    public static EmailMessage WinBack(string to, string name, string voucherCode, int percent, DateTime expires) =>
        new(to, "Quán nhớ bạn — ưu đãi quay lại",
            $"Xin chào {name},\n\nĐã lâu rồi bạn chưa ghé quán. Mã {voucherCode} giảm {percent}% còn hiệu lực đến {expires:dd/MM/yyyy}. Quay lại thưởng thức nhé!\n",
            $"<p>Xin chào <b>{System.Net.WebUtility.HtmlEncode(name)}</b>,</p><p>Đã lâu rồi bạn chưa ghé. Mã <b>{voucherCode}</b> giảm {percent}% đến {expires:dd/MM/yyyy}.</p>");

    public static EmailMessage PaymentReceipt(string to, string name, string orderCode, decimal amount, DateTime paidAt) =>
        new(to, $"Xác nhận thanh toán đơn {orderCode}",
            $"Xin chào {name},\n\nĐơn {orderCode} đã thanh toán thành công {amount:N0}đ lúc {paidAt:HH:mm dd/MM/yyyy} (UTC).\nCảm ơn bạn!\n",
            $"<p>Xin chào <b>{System.Net.WebUtility.HtmlEncode(name)}</b>,</p><p>Đơn <b>{orderCode}</b> đã thanh toán <b>{amount:N0}đ</b>.</p>");
}
