using AICOS.IdentityOrganization.Api.Infrastructure;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AICOS.IdentityOrganization.Tests;

/// <summary>SCRUM-148: outside Development the log never holds a reset code or a full email address.</summary>
public sealed class LoggingEmailSenderTests
{
    private sealed class CapturingLogger : ILogger<LoggingEmailSender>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static async Task<string> SendAsync(string environment)
    {
        var logger = new CapturingLogger();
        await new LoggingEmailSender(logger, new Env(environment)).SendAsync("nguyen.van@example.com", "Reset password", "Your code is 482913", CancellationToken.None);
        return string.Join('\n', logger.Lines);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Outside_development_the_log_has_no_code_and_no_full_address(string environment)
    {
        var log = await SendAsync(environment);

        log.Should().NotContain("482913").And.NotContain("nguyen.van").And.Contain("n***@example.com").And.Contain("Reset password");
    }

    [Fact]
    public async Task In_development_the_code_is_visible_so_a_developer_can_finish_the_flow()
    {
        var log = await SendAsync("Development");

        log.Should().Contain("482913").And.Contain("nguyen.van@example.com");
    }

    [Theory]
    [InlineData("a@b.com", "a***@b.com")]
    [InlineData("nguyen.van@example.com", "n***@example.com")]
    [InlineData("not-an-email", "***")]
    [InlineData("@x.com", "***")]
    public void Addresses_are_masked(string email, string expected) => LoggingEmailSender.Mask(email).Should().Be(expected);
}
