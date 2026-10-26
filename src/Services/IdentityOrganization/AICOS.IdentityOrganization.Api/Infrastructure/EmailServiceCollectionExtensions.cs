namespace AICOS.IdentityOrganization.Api.Infrastructure;

public static class EmailServiceCollectionExtensions
{
    /// <summary>
    /// Registers the mail sender: <see cref="SmtpEmailSender"/> when <c>Email:Smtp:Host</c> and <c>Email:FromAddress</c> are both set, otherwise
    /// <see cref="LoggingEmailSender"/> (the mails only go to the log). A host without a sender address is a half-finished setup: it is reported at start-up.
    /// </summary>
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
