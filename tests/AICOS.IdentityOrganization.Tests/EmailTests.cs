using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using AICOS.BuildingBlocks.Security;
using AICOS.IdentityOrganization.Api.Domain;
using AICOS.IdentityOrganization.Api.Features.Auth;
using AICOS.IdentityOrganization.Api.Features.Tenants;
using AICOS.IdentityOrganization.Api.Infrastructure;
using MimeKit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AICOS.IdentityOrganization.Tests;

/// <summary>The mail setup: the HTML templates, the choice between SMTP and the log from configuration, a real send over SMTP, and what happens when the mail server is down.</summary>
public sealed class EmailTests
{
    // ------------------------------------------------------------------ templates
    [Fact]
    public void The_reset_mail_carries_the_code_in_both_parts()
    {
        var mail = EmailTemplates.PasswordReset("an@example.com", "Nguyễn An", "123456", 10);

        mail.To.Should().Be("an@example.com");
        mail.Subject.Should().Be("AICOS - Mã đặt lại mật khẩu");
        mail.TextBody.Should().Contain("123456").And.Contain("10 phút").And.Contain("Xin chào Nguyễn An,");
        mail.HtmlBody.Should().Contain("123456").And.Contain("10 phút").And.StartWith("<!doctype html>");
    }

    [Fact]
    public void Values_typed_by_users_cannot_inject_markup_into_the_html_part()
    {
        var evil = "<script>alert(1)</script>\"onmouseover=\"x";

        var received = EmailTemplates.BrandRegistrationReceived("o@example.com", evil, evil);
        var rejected = EmailTemplates.BrandRejected("o@example.com", evil, evil, evil);
        var approved = EmailTemplates.BrandApproved("o@example.com", evil, evil, "https://app.example.com/\"><script>x</script>");

        foreach (var html in new[] { received.HtmlBody!, rejected.HtmlBody!, approved.HtmlBody! })
        {
            html.Should().NotContain("<script>");
            html.Should().Contain("&lt;script&gt;");
        }

        approved.HtmlBody!.Should().NotContain("\"><script>");
        rejected.TextBody.Should().Contain(evil);                                          // the plain text part is not markup: it keeps the text as it is
    }

    [Fact]
    public void The_approval_mail_links_to_the_sign_in_page_only_when_the_app_address_is_known()
    {
        var withLink = EmailTemplates.BrandApproved("o@example.com", "Chủ", "Cafe A", "https://app.example.com/");
        var withoutLink = EmailTemplates.BrandApproved("o@example.com", "Chủ", "Cafe A", null);

        withLink.TextBody.Should().Contain("https://app.example.com/login");
        withLink.HtmlBody.Should().Contain("href=\"https://app.example.com/login\"");
        withoutLink.TextBody.Should().NotContain("http");
        withoutLink.HtmlBody.Should().NotContain("<a ");
    }

    [Fact]
    public void A_rejection_mail_gives_the_reason()
    {
        var mail = EmailTemplates.BrandRejected("o@example.com", null, "Cafe B", "Thiếu giấy phép kinh doanh");

        mail.TextBody.Should().Contain("Xin chào,").And.Contain("Cafe B").And.Contain("Thiếu giấy phép kinh doanh");
        mail.HtmlBody.Should().Contain("Thiếu giấy phép kinh doanh");
    }

    // ------------------------------------------------------------------ configuration
    private static ServiceProvider Services(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new FakeEnvironment());
        services.AddAicosEmail(configuration);
        return services.BuildServiceProvider();
    }

    private sealed class FakeEnvironment : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";

        public string ApplicationName { get; set; } = "test";

        public string ContentRootPath { get; set; } = ".";

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    [Fact]
    public void Without_a_mail_server_in_the_configuration_mails_only_go_to_the_log()
    {
        using var empty = Services([]);
        using var hostOnly = Services(new() { ["Email:Smtp:Host"] = "smtp.example.com" });          // no sender address: half a setup
        using var addressOnly = Services(new() { ["Email:FromAddress"] = "no-reply@example.com" });

        empty.GetRequiredService<IEmailSender>().Should().BeOfType<LoggingEmailSender>();
        hostOnly.GetRequiredService<IEmailSender>().Should().BeOfType<LoggingEmailSender>();
        addressOnly.GetRequiredService<IEmailSender>().Should().BeOfType<LoggingEmailSender>();
    }

    [Fact]
    public void With_a_host_and_a_sender_address_mails_go_through_smtp_and_the_options_are_read()
    {
        using var services = Services(new()
        {
            ["Email:Smtp:Host"] = "smtp.example.com", ["Email:Smtp:Port"] = "465", ["Email:Smtp:Security"] = "Ssl", ["Email:Smtp:Username"] = "user",
            ["Email:FromAddress"] = "no-reply@example.com", ["Email:FromName"] = "AICOS Cafe", ["Email:AppUrl"] = "https://app.example.com",
        });

        services.GetRequiredService<IEmailSender>().Should().BeOfType<SmtpEmailSender>();
        var options = services.GetRequiredService<IOptions<EmailOptions>>().Value;
        (options.Smtp.Host, options.Smtp.Port, options.FromName, options.AppUrl).Should().Be(("smtp.example.com", 465, "AICOS Cafe", "https://app.example.com"));
        options.Smtp.SocketOptions.Should().Be(MailKit.Security.SecureSocketOptions.SslOnConnect);
        new SmtpOptions { Security = "none" }.SocketOptions.Should().Be(MailKit.Security.SecureSocketOptions.None);
        new SmtpOptions().SocketOptions.Should().Be(MailKit.Security.SecureSocketOptions.StartTls);
        options.Smtp.Password.Should().BeEmpty();                                           // no secret has a default
    }

    // ------------------------------------------------------------------ a real send over SMTP
    [Fact]
    public async Task A_mail_is_sent_to_the_smtp_server_with_a_text_and_an_html_part()
    {
        await using var server = new FakeSmtpServer();
        var options = Options.Create(new EmailOptions
        {
            FromAddress = "no-reply@example.com", FromName = "AICOS",
            Smtp = { Host = "127.0.0.1", Port = server.Port, Security = "None", TimeoutSeconds = 5 },
        });
        var sender = new SmtpEmailSender(options, NullLogger<SmtpEmailSender>.Instance);

        await sender.SendAsync(EmailTemplates.PasswordReset("an@example.com", "Nguyễn An", "654321", 10), CancellationToken.None);

        var received = server.Received.Should().ContainSingle().Subject;
        received.From.Should().Be("no-reply@example.com");
        received.Recipients.Should().Equal("an@example.com");
        var mime = MimeMessage.Load(new MemoryStream(Encoding.UTF8.GetBytes(received.Data)));
        mime.Subject.Should().Be("AICOS - Mã đặt lại mật khẩu");
        mime.From.Mailboxes.Single().Name.Should().Be("AICOS");
        mime.TextBody.Should().Contain("654321");
        mime.HtmlBody.Should().Contain("654321").And.Contain("Mã đặt lại mật khẩu");
    }

    [Fact]
    public async Task A_smtp_failure_is_reported_to_the_caller_without_the_address_or_secret_in_the_log()
    {
        var log = new CapturingLogger<SmtpEmailSender>();
        var options = Options.Create(new EmailOptions
        {
            FromAddress = "no-reply@example.com",
            Smtp = { Host = "127.0.0.1", Port = 1, Security = "None", Username = "u", Password = "s3cret-pass", TimeoutSeconds = 3 },        // nothing listens here
        });
        var sender = new SmtpEmailSender(options, log);

        var send = () => sender.SendAsync(EmailTemplates.PasswordReset("an@example.com", null, "111222", 10), CancellationToken.None);

        await send.Should().ThrowAsync<Exception>();
        var text = string.Join('\n', log.Lines);
        text.Should().Contain("could not be sent").And.Contain("a***@example.com");
        text.Should().NotContain("an@example.com").And.NotContain("s3cret-pass").And.NotContain("111222");
    }

    // ------------------------------------------------------------------ the flows that send mail
    [Fact]
    public async Task Forgot_password_mails_the_code_with_the_html_template_and_the_answer_does_not_depend_on_the_mail_server()
    {
        using var factory = new IdentityApiFactory();
        var client = factory.CreateClient();
        await AuthEndpointsTests.SignUpAsync(client, factory, "an@example.com", "Brand1234", fullName: "Nguyễn An");
        factory.Emails.Messages.Clear();

        var ok = await client.PostAsJsonAsync("/api/v1/identity/auth/forgot-password", new ForgotPasswordRequest("an@example.com"), AuthEndpointsTests.Json);

        ok.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var mail = factory.Emails.Messages.Single();
        (mail.To, mail.Subject).Should().Be(("an@example.com", "AICOS - Mã đặt lại mật khẩu"));
        mail.HtmlBody.Should().Contain(factory.Emails.LastCodeFor("an@example.com"));

        await Task.Delay(1100);
        factory.Emails.Fail = new IOException("the mail server is down");
        var down = await client.PostAsJsonAsync("/api/v1/identity/auth/forgot-password", new ForgotPasswordRequest("an@example.com"), AuthEndpointsTests.Json);
        var unknown = await client.PostAsJsonAsync("/api/v1/identity/auth/forgot-password", new ForgotPasswordRequest("nobody@example.com"), AuthEndpointsTests.Json);

        down.StatusCode.Should().Be(HttpStatusCode.Accepted);                              // the same answer as for an unknown address: no way to tell
        (await down.Content.ReadAsStringAsync()).Should().Be(await unknown.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_brand_registration_flow_sends_the_received_approved_and_refused_mails()
    {
        using var factory = new IdentityApiFactory();
        var anonymous = factory.CreateClient();
        using var superAdmin = factory.CreateClientAs(AicosRoles.SuperAdmin);
        string[] codes = ["brand-ok", "brand-no"];
        foreach (var code in codes)
        {
            (await anonymous.PostAsJsonAsync("/api/v1/identity/auth/register-brand",
                new RegisterBrandRequest($"Quán {code}", code, "Chủ quán", $"{code}@example.com", null, "Brand1234", "Brand1234", null), AuthEndpointsTests.Json)).EnsureSuccessStatusCode();
        }

        var tenants = (await superAdmin.GetFromJsonAsync<AICOS.BuildingBlocks.Persistence.PagedResponse<TenantResponse>>("/api/v1/identity/tenants?status=Pending", AuthEndpointsTests.Json))!.Items;
        await superAdmin.PostAsJsonAsync($"/api/v1/identity/tenants/{tenants.Single(t => t.Code == "brand-ok").Id}/approve", new { });
        await superAdmin.PostAsJsonAsync($"/api/v1/identity/tenants/{tenants.Single(t => t.Code == "brand-no").Id}/reject", new { reason = "Thiếu giấy phép" });

        var mails = factory.Emails.Messages.ToList();
        mails.Where(m => m.Subject == "AICOS - Đã nhận đăng ký thương hiệu").Select(m => m.To).Should().BeEquivalentTo("brand-ok@example.com", "brand-no@example.com");
        mails.Single(m => m.Subject == "AICOS - Đăng ký thương hiệu đã được duyệt").To.Should().Be("brand-ok@example.com");
        var refused = mails.Single(m => m.Subject == "AICOS - Đăng ký thương hiệu chưa được duyệt");
        (refused.To, refused.HtmlBody).Should().Be(("brand-no@example.com", refused.HtmlBody));
        refused.TextBody.Should().Contain("Thiếu giấy phép");
        mails.Should().OnlyContain(m => m.HtmlBody != null);
    }

    [Fact]
    public async Task A_mail_server_that_is_down_does_not_undo_a_decision_on_a_registration()
    {
        using var factory = new IdentityApiFactory();
        var anonymous = factory.CreateClient();
        using var superAdmin = factory.CreateClientAs(AicosRoles.SuperAdmin);
        factory.Emails.Fail = new IOException("the mail server is down");

        var registered = await anonymous.PostAsJsonAsync("/api/v1/identity/auth/register-brand",
            new RegisterBrandRequest("Quán A", "brand-a", "Chủ", "a@example.com", null, "Brand1234", "Brand1234", null), AuthEndpointsTests.Json);
        registered.StatusCode.Should().Be(HttpStatusCode.Created);
        var tenant = (await superAdmin.GetFromJsonAsync<AICOS.BuildingBlocks.Persistence.PagedResponse<TenantResponse>>("/api/v1/identity/tenants?status=Pending", AuthEndpointsTests.Json))!.Items.Single();

        var approved = await superAdmin.PostAsJsonAsync($"/api/v1/identity/tenants/{tenant.Id}/approve", new { });

        approved.StatusCode.Should().Be(HttpStatusCode.OK);
        (await approved.Content.ReadFromJsonAsync<TenantResponse>(AuthEndpointsTests.Json))!.Status.Should().Be(TenantStatus.Active);
    }
}

/// <summary>Collects what the code under test logs, to check that nothing secret reaches the log.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<string> Lines { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Lines.Add(formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
}

/// <summary>A minimal SMTP server on a free local port: greets, accepts one message at a time and keeps what it received.</summary>
internal sealed class FakeSmtpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public FakeSmtpServer()
    {
        _listener.Start();
        _loop = Task.Run(AcceptAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public List<ReceivedMail> Received { get; } = [];

    public sealed record ReceivedMail(string From, List<string> Recipients, string Data);

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                await ServeAsync(client);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // the test ended
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };
        await writer.WriteLineAsync("220 fake.smtp ready");
        string from = string.Empty;
        var recipients = new List<string>();
        while (await reader.ReadLineAsync() is { } line)
        {
            var command = line.ToUpperInvariant();
            if (command.StartsWith("EHLO") || command.StartsWith("HELO"))
            {
                await writer.WriteLineAsync("250-fake.smtp");
                await writer.WriteLineAsync("250 8BITMIME");
            }
            else if (command.StartsWith("MAIL FROM"))
            {
                from = line[(line.IndexOf('<') + 1)..line.IndexOf('>')];
                await writer.WriteLineAsync("250 ok");
            }
            else if (command.StartsWith("RCPT TO"))
            {
                recipients.Add(line[(line.IndexOf('<') + 1)..line.IndexOf('>')]);
                await writer.WriteLineAsync("250 ok");
            }
            else if (command == "DATA")
            {
                await writer.WriteLineAsync("354 go ahead");
                var data = new StringBuilder();
                while (await reader.ReadLineAsync() is { } part && part != ".")
                {
                    data.AppendLine(part.StartsWith("..") ? part[1..] : part);
                }

                lock (Received)
                {
                    Received.Add(new ReceivedMail(from, [.. recipients], data.ToString()));
                }

                await writer.WriteLineAsync("250 queued");
            }
            else if (command == "QUIT")
            {
                await writer.WriteLineAsync("221 bye");
                return;
            }
            else
            {
                await writer.WriteLineAsync("250 ok");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await Task.WhenAny(_loop, Task.Delay(1000));
    }
}
