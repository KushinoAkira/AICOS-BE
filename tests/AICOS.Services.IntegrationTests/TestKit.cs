using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AICOS.BuildingBlocks.Http;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Persistence;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace AICOS.Services.IntegrationTests;

/// <summary>
/// Boots one microservice in memory: EF in-memory database (fresh per factory), recording event bus instead of Redis,
/// and a controllable clock for time-based rules.
/// </summary>
public sealed class ServiceFactory<TService, TDbContext>(Action<IServiceCollection>? configure = null) : WebApplicationFactory<TService>
    where TService : class
    where TDbContext : DbContext
{
    /// <summary>The brand every test token belongs to; other services do not filter by tenant yet.</summary>
    public static readonly Guid DefaultTenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly string _databaseName = $"tests-{Guid.NewGuid():N}";

    public RecordingEventBus Events { get; } = new();

    public TestClock Clock { get; } = new();

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<TDbContext>>();
            services.AddDbContext<TDbContext>((sp, options) => options
                .UseInMemoryDatabase(_databaseName)
                .AddInterceptors(sp.GetRequiredService<AuditableEntityInterceptor>()));


            // No Redis in tests: events are recorded by RecordingEventBus and handlers are invoked directly.
            // Left running, the subscriber retries the connection for seconds and then stops the whole test host.
            foreach (var subscriber in services.Where(s => s.ImplementationType?.Name == "RedisEventBusSubscriber").ToList())
            {
                services.Remove(subscriber);
            }
            services.RemoveAll<IEventBus>();
            services.AddSingleton<IEventBus>(Events);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);

            configure?.Invoke(services);
        });
    }

    /// <summary>Client with a JWT for the given role. Tokens use the real clock so moving <see cref="Clock"/> never invalidates them.</summary>
    public HttpClient As(string role, Guid? branchId = null, Guid? userId = null, Guid? tenantId = null)
    {
        var client = CreateClient();
        var jwt = new JwtTokenService(Services.GetRequiredService<IOptions<JwtOptions>>(), TimeProvider.System);
        var (token, _) = jwt.CreateAccessToken(userId ?? Guid.NewGuid(), $"Test {role}", role, branchId, role == AicosRoles.SuperAdmin ? null : tenantId ?? DefaultTenantId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public async Task HandleAsync<TEvent>(TEvent @event)
        where TEvent : IntegrationEvent
    {
        using var scope = Services.CreateScope();
        foreach (var handler in scope.ServiceProvider.GetServices<IIntegrationEventHandler<TEvent>>())
        {
            await handler.HandleAsync(@event, CancellationToken.None);
        }
    }
}

public sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => Now;
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

public static class Http
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(), new TimeOnlyJsonConverter() },
    };

    public static Task<HttpResponseMessage> Post(this HttpClient client, string url, object body) => client.PostAsJsonAsync(url, body, Json);

    public static Task<HttpResponseMessage> Put(this HttpClient client, string url, object body) => client.PutAsJsonAsync(url, body, Json);

    public static Task<HttpResponseMessage> Patch(this HttpClient client, string url, object body) => client.PatchAsJsonAsync(url, body, Json);

    public static async Task<T> Read<T>(this HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(Json))!;

    public static async Task<JsonElement> Body(this HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    /// <summary>POST that must succeed; returns the parsed body (fails the test with the response text otherwise).</summary>
    public static async Task<JsonElement> PostOk(this HttpClient client, string url, object body)
    {
        var response = await client.Post(url, body);
        var text = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue($"POST {url} returned {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    public static Guid Id(this JsonElement element) => element.GetProperty("id").GetGuid();

    public static async Task ShouldBe(this Task<HttpResponseMessage> request, int status, string? errorCode = null)
    {
        var response = await request;
        var text = await response.Content.ReadAsStringAsync();
        ((int)response.StatusCode).Should().Be(status, text);
        if (errorCode is not null)
        {
            JsonDocument.Parse(text).RootElement.GetProperty("errorCode").GetString().Should().Be(errorCode);
        }
    }
}
