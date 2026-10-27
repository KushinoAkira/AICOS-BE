using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Persistence;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts;
using AICOS.IdentityOrganization.Api;
using AICOS.IdentityOrganization.Api.Domain;
using AICOS.IdentityOrganization.Api.Infrastructure;
using AICOS.IdentityOrganization.Api.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AICOS.IdentityOrganization.Tests;

/// <summary>
/// Boots the Identity service with an in-memory database (one fresh database per factory instance),
/// a recording event bus instead of Redis and a capturing email sender instead of the log.
/// </summary>
public sealed class IdentityApiFactory : WebApplicationFactory<IdentityOrganizationService>
{
    /// <summary>The brand every test account belongs to unless a test says otherwise (see <see cref="SeedTenant"/>).</summary>
    public static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public const string TenantCode = "test-brand";

    private readonly string _databaseName = $"identity-tests-{Guid.NewGuid():N}";

    public RecordingEventBus Events { get; } = new();

    public CapturingEmailSender Emails { get; } = new();

    public TestClock Clock { get; } = new();

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<IdentityOrganizationDbContext>>();
            services.AddDbContext<IdentityOrganizationDbContext>((sp, options) => options
                .UseInMemoryDatabase(_databaseName)
                .AddInterceptors(sp.GetRequiredService<AuditableEntityInterceptor>()));


            // No Redis in tests: events are recorded by RecordingEventBus and handlers are invoked directly.
            // Left running, the subscriber retries the connection for seconds and then stops the whole test host.
            foreach (var subscriber in services.Where(s => s.ImplementationType?.Name == "RedisEventBusSubscriber").ToList())
            {
                services.Remove(subscriber);
            }
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);

            services.RemoveAll<IEventBus>();
            services.AddSingleton<IEventBus>(Events);
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(Emails);
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);
        SeedTenant(host.Services);
        return host;
    }

    /// <summary>Adds the default test brand (id <see cref="TenantId"/>, code <see cref="TenantCode"/>).</summary>
    private static void SeedTenant(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityOrganizationDbContext>();
        var tenant = new Tenant("Test Brand", TenantCode);
        typeof(Tenant).GetProperty(nameof(Tenant.Id))!.SetValue(tenant, TenantId);
        db.Tenants.Add(tenant);
        db.SaveChanges();
    }

    /// <summary>A client carrying a /dev/token-style JWT (random user id, no account behind it).</summary>
    public HttpClient CreateClientAs(string role, Guid? branchId = null, Guid? userId = null, Guid? tenantId = null)
    {
        var client = CreateClient();
        // Real clock: moving Clock forward must never make test tokens "not yet valid".
        var (token, _) = new JwtTokenService(Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<JwtOptions>>(), TimeProvider.System)
            .CreateAccessToken(
                userId ?? Guid.NewGuid(), $"Test {role}", role, branchId, role == AicosRoles.SuperAdmin ? null : tenantId ?? TenantId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}

public sealed class RecordingEventBus : IEventBus
{
    public ConcurrentQueue<IntegrationEvent> Published { get; } = new();

    public Task PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
        where TEvent : IntegrationEvent
    {
        Published.Enqueue(@event);
        return Task.CompletedTask;
    }
}

public sealed partial class CapturingEmailSender : IEmailSender
{
    public ConcurrentQueue<(string To, string Subject, string Body)> Sent { get; } = new();

    public Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken)
    {
        Sent.Enqueue((to, subject, body));
        return Task.CompletedTask;
    }

    /// <summary>The whole message (with its HTML part) of every mail sent through <see cref="IEmailSender.SendAsync(EmailMessage, CancellationToken)"/>.</summary>
    public ConcurrentQueue<EmailMessage> Messages { get; } = new();

    /// <summary>Set to simulate a mail server that is down.</summary>
    public Exception? Fail { get; set; }

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        if (Fail is not null)
        {
            throw Fail;
        }

        Messages.Enqueue(message);
        Sent.Enqueue((message.To, message.Subject, message.TextBody));
        return Task.CompletedTask;
    }

    public string LastCodeFor(string email) =>
        SixDigits().Match(Sent.Last(m => m.To == email).Body).Value;

    [GeneratedRegex(@"\b\d{6}\b")]
    private static partial Regex SixDigits();
}

/// <summary>Real time plus an offset the tests can move forward, so billing dates can be crossed without waiting.</summary>
public sealed class TestClock : TimeProvider
{
    public TimeSpan Offset { get; private set; }

    public void Advance(TimeSpan by) => Offset += by;

    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + Offset;
}
