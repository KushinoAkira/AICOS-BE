using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AICOS.BuildingBlocks.Caching;
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

    public InMemoryCacheStore Cache { get; } = new();

    public InMemoryHashStore Hashes { get; } = new();

    /// <summary>The branches that exist, and the tenant each belongs to. Tests add the ones they use.</summary>
    public BranchRegistry Branches { get; } = new();

    /// <summary>The login accounts that exist (see <see cref="UserRegistry"/>).</summary>
    public UserRegistry Users { get; } = new();

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

            // Branch checks and the Redis cache have test doubles: no Identity service and no Redis in tests.
            services.RemoveAll<ICacheStore>();
            services.AddSingleton<ICacheStore>(Cache);
            services.RemoveAll<IHashStore>();
            services.AddSingleton<IHashStore>(Hashes);
            services.RemoveAll<IBranchDirectory>();
            services.AddSingleton(Branches);
            services.AddScoped<IBranchDirectory, FakeBranchDirectory>();
            services.RemoveAll<IUserDirectory>();
            services.AddSingleton(Users);
            services.AddScoped<IUserDirectory, FakeUserDirectory>();

            configure?.Invoke(services);
        });
    }

    /// <summary>Called with every request made through a client from <see cref="As"/>, before it is sent (tests use it to register orders on the fly).</summary>
    public Func<HttpRequestMessage, Task>? Observer { get; set; }

    /// <summary>Client with a JWT for the given role. Tokens use the real clock so moving <see cref="Clock"/> never invalidates them.</summary>
    public HttpClient As(string role, Guid? branchId = null, Guid? userId = null, Guid? tenantId = null)
    {
        var client = Observer is null ? CreateClient() : CreateDefaultClient(new ObservingHandler(() => Observer));
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

/// <summary>Stands in for the Identity service: which branches exist, the tenant they belong to, and whether they are active.</summary>
public sealed class BranchRegistry
{
    private readonly ConcurrentDictionary<Guid, (Guid TenantId, string Name, bool IsActive)> _branches = new();

    public Guid Add(Guid? tenantId = null, bool active = true, string? name = null, Guid? id = null)
    {
        var branchId = id ?? Guid.NewGuid();
        _branches[branchId] = (tenantId ?? ServiceFactory<object, DbContext>.DefaultTenantId, name ?? $"Branch {branchId.ToString()[..4]}", active);
        return branchId;
    }

    public void Suspend(Guid branchId) => _branches[branchId] = _branches[branchId] with { IsActive = false };

    public bool TryGet(Guid branchId, out (Guid TenantId, string Name, bool IsActive) branch) => _branches.TryGetValue(branchId, out branch);
}

/// <summary>Like the real directory, a branch of another tenant does not exist as far as the caller is concerned.</summary>
internal sealed class FakeBranchDirectory(BranchRegistry registry, ICurrentUser user) : IBranchDirectory
{
    public Task<BranchInfo> GetAsync(Guid branchId, CancellationToken cancellationToken = default)
    {
        if (!registry.TryGet(branchId, out var branch) || branch.TenantId != user.TenantId)
        {
            throw new AICOS.BuildingBlocks.Exceptions.ValidationFailedException("branchId", $"Branch '{branchId}' does not exist in your brand.");
        }

        return Task.FromResult(new BranchInfo(branchId, branch.Name, branch.IsActive));
    }
}

/// <summary>Stands in for the Identity service: which login accounts exist, their role, branch and tenant.</summary>
public sealed class UserRegistry
{
    private readonly ConcurrentDictionary<Guid, (Guid TenantId, string FullName, string Role, Guid? BranchId, bool IsActive)> _users = new();

    public Guid Add(string role, Guid? branchId, string fullName, Guid? tenantId = null, bool active = true, Guid? id = null)
    {
        var userId = id ?? Guid.NewGuid();
        _users[userId] = (tenantId ?? ServiceFactory<object, DbContext>.DefaultTenantId, fullName, role, branchId, active);
        return userId;
    }

    public bool TryGet(Guid userId, out (Guid TenantId, string FullName, string Role, Guid? BranchId, bool IsActive) user) => _users.TryGetValue(userId, out user);
}

/// <summary>
/// Like Identity: an account of another tenant does not exist for the caller, and a BranchManager only sees the Barista and Cashier
/// accounts of their own branch.
/// </summary>
internal sealed class FakeUserDirectory(UserRegistry registry, ICurrentUser caller) : IUserDirectory
{
    public Task<UserInfo> GetAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var visible = registry.TryGet(userId, out var user) && user.TenantId == caller.TenantId;
        if (visible && caller.IsInRole(AicosRoles.BranchManager))
        {
            visible = user.BranchId == caller.BranchId && user.Role is AicosRoles.Barista or AicosRoles.Cashier;
        }

        if (!visible)
        {
            throw new AICOS.BuildingBlocks.Exceptions.ValidationFailedException("userId", $"User account '{userId}' does not exist (or is not one of your staff).");
        }

        return Task.FromResult(new UserInfo(userId, user.FullName, user.Role, user.BranchId, user.IsActive));
    }
}

internal sealed class ObservingHandler(Func<Func<HttpRequestMessage, Task>?> observer) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is not null)
        {
            await request.Content.LoadIntoBufferAsync();
        }

        var current = observer();
        if (current is not null)
        {
            await current(request);
        }

        return await base.SendAsync(request, cancellationToken);
    }
}

/// <summary>
/// Stands in for the Order service in Payment tests. An order that nobody registered is created on the fly from the first payment request that
/// names it (see <see cref="PaymentTestBase"/>), so a test only registers an order when it wants a specific amount, status or branch.
/// </summary>
public sealed class FakeOrderDirectory : AICOS.Payments.Api.Infrastructure.IOrderDirectory
{
    private readonly Dictionary<Guid, AICOS.Payments.Api.Infrastructure.OrderInfo> _orders = [];

    public List<AICOS.Payments.Api.Features.Payments.OrderForReconciliation> Listed { get; } = [];

    public int Lookups { get; private set; }

    public void Set(Guid id, Guid branchId, decimal amount, string status = "Pending", string paymentStatus = "Unpaid") =>
        _orders[id] = new AICOS.Payments.Api.Infrastructure.OrderInfo(id, branchId, amount, status, paymentStatus);

    public bool Knows(Guid id) => _orders.ContainsKey(id);

    public Task<AICOS.Payments.Api.Infrastructure.OrderInfo> GetAsync(Guid orderId, CancellationToken ct)
    {
        Lookups++;
        return _orders.TryGetValue(orderId, out var order)
            ? Task.FromResult(order)
            : throw new AICOS.BuildingBlocks.Exceptions.BusinessRuleException("payment.order_not_found", "That order does not exist, or it is not yours to pay.");
    }

    public Task<IReadOnlyList<AICOS.Payments.Api.Features.Payments.OrderForReconciliation>> ListForReconciliationAsync(Guid? branchId, DateOnly from, DateOnly to, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AICOS.Payments.Api.Features.Payments.OrderForReconciliation>>(Listed.ToList());
}
