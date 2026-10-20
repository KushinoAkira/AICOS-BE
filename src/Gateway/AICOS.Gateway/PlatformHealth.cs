using System.Collections.Concurrent;
using System.Diagnostics;
using AICOS.BuildingBlocks.Security;

namespace AICOS.Gateway;

/// <summary>Request counters of one service as seen by the gateway: volume, server errors and latency.</summary>
public sealed class ServiceCounter
{
    private const int WindowSize = 500;

    private readonly object _lock = new();
    private readonly Queue<double> _recent = new();
    private long _requests;
    private long _serverErrors;
    private double _totalMs;
    private double _maxMs;

    public void Record(int statusCode, double elapsedMs)
    {
        lock (_lock)
        {
            _requests++;
            if (statusCode >= 500)
            {
                _serverErrors++;
            }

            _totalMs += elapsedMs;
            _maxMs = Math.Max(_maxMs, elapsedMs);
            _recent.Enqueue(elapsedMs);
            if (_recent.Count > WindowSize)
            {
                _recent.Dequeue();
            }
        }
    }

    public ServiceTraffic Snapshot()
    {
        lock (_lock)
        {
            var sorted = _recent.Order().ToArray();
            var p95 = sorted.Length == 0 ? 0 : sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1];
            return new ServiceTraffic(
                _requests,
                _serverErrors,
                _requests == 0 ? 0 : Math.Round((double)_serverErrors / _requests, 4),
                _requests == 0 ? 0 : Math.Round(_totalMs / _requests, 1),
                Math.Round(p95, 1),
                Math.Round(_maxMs, 1));
        }
    }
}

public sealed record ServiceTraffic(long Requests, long ServerErrors, double ErrorRate, double AverageLatencyMs, double P95LatencyMs, double MaxLatencyMs);

/// <summary>
/// In-memory traffic counters per service (UC-09: errors and API latency). They start from zero each time the gateway
/// starts and are kept per route prefix, so they are cheap and need no storage; long-term history belongs to monitoring tools.
/// </summary>
public sealed class GatewayMetrics
{
    private readonly ConcurrentDictionary<string, ServiceCounter> _services = new(StringComparer.OrdinalIgnoreCase);

    public DateTime StartedAtUtc { get; } = DateTime.UtcNow;

    public void Record(string service, int statusCode, double elapsedMs) =>
        _services.GetOrAdd(service, _ => new ServiceCounter()).Record(statusCode, elapsedMs);

    public ServiceTraffic For(string service) =>
        _services.TryGetValue(service, out var counter) ? counter.Snapshot() : new ServiceTraffic(0, 0, 0, 0, 0, 0);
}

/// <summary>Records how long each proxied call to <c>/api/v1/{prefix}/...</c> took and whether it failed with a 5xx.</summary>
public sealed class RequestMetricsMiddleware(RequestDelegate next, GatewayMetrics metrics, RoutePrefixMap prefixes)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var service = prefixes.ServiceFor(context.Request.Path);
        if (service is null)
        {
            await next(context);
            return;
        }

        var started = Stopwatch.GetTimestamp();
        var failed = false;
        try
        {
            await next(context);
        }
        catch
        {
            failed = true; // the exception handler further out turns this into a 500; it has not run yet, so count it here
            throw;
        }
        finally
        {
            metrics.Record(service, failed ? StatusCodes.Status500InternalServerError : context.Response.StatusCode, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }
}

/// <summary>Maps <c>/api/v1/{prefix}</c> to the gateway cluster name, read from the same ReverseProxy configuration YARP uses.</summary>
public sealed class RoutePrefixMap
{
    private readonly Dictionary<string, string> _clusterByPrefix = new(StringComparer.OrdinalIgnoreCase);

    public RoutePrefixMap(IConfiguration configuration)
    {
        foreach (var route in configuration.GetSection("ReverseProxy:Routes").GetChildren())
        {
            var path = route["Match:Path"];
            var cluster = route["ClusterId"];
            if (path is null || cluster is null)
            {
                continue;
            }

            var segments = path.Trim('/').Split('/');
            if (segments is ["api", "v1", { } prefix, ..])
            {
                _clusterByPrefix[prefix] = cluster;
            }
        }
    }

    public string? ServiceFor(PathString path)
    {
        var segments = path.Value?.Trim('/').Split('/');
        return segments is ["api", "v1", { } prefix, ..] && _clusterByPrefix.TryGetValue(prefix, out var cluster) ? cluster : null;
    }
}

public sealed record ServiceHealth(string Service, string Address, string Status, int? HttpStatus, double LatencyMs, string? Error, ServiceTraffic Traffic);

public sealed record PlatformHealthResponse(
    string Status, DateTime CheckedAtUtc, DateTime GatewayStartedAtUtc, double GatewayUptimeMinutes, IReadOnlyList<ServiceHealth> Services);

/// <summary>
/// UC-09 Monitor Platform Health Status: GET /api/v1/platform/health, SuperAdmin only. Asks every service's
/// <c>/health/ready</c> (process, PostgreSQL and Redis reachable: what a container health check sees) and adds the traffic
/// the gateway has seen for it. Overall status: Healthy when all answer, Degraded when some do not, Unhealthy when none do.
/// </summary>
public static class PlatformHealthEndpoints
{
    public const string HttpClientName = "platform-health";

    public static void MapPlatformHealth(this WebApplication app)
    {
        app.MapGet("/api/v1/platform/health", CheckAsync)
            .WithTags("Platform")
            .RequireAuthorization(AicosPolicies.PlatformAdmin);
    }

    private static async Task<PlatformHealthResponse> CheckAsync(
        IConfiguration configuration, IHttpClientFactory httpClients, GatewayMetrics metrics, TimeProvider clock, CancellationToken ct)
    {
        var clusters = configuration.GetSection("ReverseProxy:Clusters").GetChildren()
            .Select(c => (Name: c.Key, Address: c["Destinations:primary:Address"]))
            .Where(c => !string.IsNullOrWhiteSpace(c.Address))
            .OrderBy(c => c.Name)
            .ToList();

        var checks = await Task.WhenAll(clusters.Select(c => ProbeAsync(httpClients, metrics, c.Name, c.Address!, ct)));

        var healthy = checks.Count(c => c.Status == "Healthy");
        var overall = healthy == checks.Length ? "Healthy" : healthy == 0 ? "Unhealthy" : "Degraded";
        var now = clock.GetUtcNow().UtcDateTime;

        return new PlatformHealthResponse(overall, now, metrics.StartedAtUtc, Math.Round((now - metrics.StartedAtUtc).TotalMinutes, 1), checks);
    }

    private static async Task<ServiceHealth> ProbeAsync(
        IHttpClientFactory httpClients, GatewayMetrics metrics, string service, string address, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            using var client = httpClients.CreateClient(HttpClientName);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));

            using var response = await client.GetAsync($"{address.TrimEnd('/')}/health/ready", timeout.Token);
            return new ServiceHealth(
                service, address, response.IsSuccessStatusCode ? "Healthy" : "Unhealthy", (int)response.StatusCode,
                Elapsed(started), response.IsSuccessStatusCode ? null : "Readiness check failed", metrics.For(service));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            return new ServiceHealth(
                service, address, "Unhealthy", null, Elapsed(started), ex is HttpRequestException ? "Not reachable" : "Timed out", metrics.For(service));
        }
    }

    private static double Elapsed(long started) => Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1);
}
