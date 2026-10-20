using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Gateway;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AICOS.Services.SmokeTests;

/// <summary>UC-09 Monitor Platform Health Status: GET /api/v1/platform/health served by the gateway.</summary>
public class PlatformHealthTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Url = "/api/v1/platform/health";

    private readonly WebApplicationFactory<Program> _factory;

    public PlatformHealthTests(WebApplicationFactory<Program> factory) => _factory = factory;

    /// <summary>Answers every readiness probe from a table keyed by port, so the result never depends on what runs on the machine.</summary>
    private sealed class StubServices(Func<int, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<Uri> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requested)
            {
                Requested.Add(request.RequestUri!);
            }

            return Task.FromResult(answer(request.RequestUri!.Port));
        }
    }

    private HttpClient ClientAs(string role, StubServices? stub = null, GatewayMetrics? metrics = null)
    {
        var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            if (stub is not null)
            {
                services.AddHttpClient(PlatformHealthEndpoints.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => stub);
            }

            if (metrics is not null)
            {
                services.AddSingleton(metrics);
            }
        }));

        var client = factory.CreateClient();
        var tokens = factory.Services.GetRequiredService<JwtTokenService>();
        var (token, _) = tokens.CreateAccessToken(Guid.NewGuid(), $"Test {role}", role, tenantId: role == AicosRoles.SuperAdmin ? null : Guid.NewGuid());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Anonymous_callers_are_rejected()
    {
        using var client = _factory.CreateClient();

        (await client.GetAsync(Url)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(AicosRoles.BrandAdmin)]
    [InlineData(AicosRoles.BranchManager)]
    [InlineData(AicosRoles.Cashier)]
    [InlineData(AicosRoles.Customer)]
    public async Task Only_the_super_admin_can_see_platform_health(string role)
    {
        using var client = ClientAs(role, new StubServices(_ => new HttpResponseMessage(HttpStatusCode.OK)));

        (await client.GetAsync(Url)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task All_services_answering_means_healthy()
    {
        var stub = new StubServices(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = ClientAs(AicosRoles.SuperAdmin, stub);

        var body = await client.GetFromJsonAsync<JsonElement>(Url);

        body.GetProperty("status").GetString().Should().Be("Healthy");
        var services = body.GetProperty("services").EnumerateArray().ToList();
        services.Select(s => s.GetProperty("service").GetString()).Should()
            .BeEquivalentTo("identity", "menu", "table", "order", "payment", "kitchen", "workforce", "reporting", "customer", "ai");
        services.Should().OnlyContain(s => s.GetProperty("status").GetString() == "Healthy" && s.GetProperty("httpStatus").GetInt32() == 200);
        stub.Requested.Should().OnlyContain(u => u.AbsolutePath == "/health/ready"); // readiness: process, database and Redis
        body.GetProperty("checkedAtUtc").GetDateTime().Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Some_services_failing_means_degraded_and_each_failure_is_explained()
    {
        var stub = new StubServices(port => port switch
        {
            5102 => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            5103 => throw new HttpRequestException("connection refused"),
            _ => new HttpResponseMessage(HttpStatusCode.OK),
        });
        using var client = ClientAs(AicosRoles.SuperAdmin, stub);

        var body = await client.GetFromJsonAsync<JsonElement>(Url);

        body.GetProperty("status").GetString().Should().Be("Degraded");
        var byName = body.GetProperty("services").EnumerateArray().ToDictionary(s => s.GetProperty("service").GetString()!);
        byName["menu"].GetProperty("status").GetString().Should().Be("Unhealthy");
        byName["menu"].GetProperty("httpStatus").GetInt32().Should().Be(503);
        byName["menu"].GetProperty("error").GetString().Should().Be("Readiness check failed");
        byName["table"].GetProperty("status").GetString().Should().Be("Unhealthy");
        byName["table"].GetProperty("httpStatus").ValueKind.Should().Be(JsonValueKind.Null);
        byName["table"].GetProperty("error").GetString().Should().Be("Not reachable");
        byName["identity"].GetProperty("status").GetString().Should().Be("Healthy");
        byName["identity"].GetProperty("error").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task No_service_answering_means_unhealthy_not_an_error()
    {
        var stub = new StubServices(_ => throw new HttpRequestException("down"));
        using var client = ClientAs(AicosRoles.SuperAdmin, stub);

        var response = await client.GetAsync(Url);

        response.StatusCode.Should().Be(HttpStatusCode.OK); // the check itself worked; the platform is what is down
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString().Should().Be("Unhealthy");
    }

    [Fact]
    public async Task The_report_includes_the_traffic_the_gateway_saw_for_each_service()
    {
        var metrics = new GatewayMetrics();
        metrics.Record("identity", 200, 10);
        metrics.Record("identity", 500, 30);
        metrics.Record("identity", 200, 20);
        using var client = ClientAs(AicosRoles.SuperAdmin, new StubServices(_ => new HttpResponseMessage(HttpStatusCode.OK)), metrics);

        var body = await client.GetFromJsonAsync<JsonElement>(Url);

        var identity = body.GetProperty("services").EnumerateArray().Single(s => s.GetProperty("service").GetString() == "identity").GetProperty("traffic");
        identity.GetProperty("requests").GetInt64().Should().Be(3);
        identity.GetProperty("serverErrors").GetInt64().Should().Be(1);
        identity.GetProperty("errorRate").GetDouble().Should().BeApproximately(0.3333, 0.0001);
        identity.GetProperty("averageLatencyMs").GetDouble().Should().Be(20);
        identity.GetProperty("maxLatencyMs").GetDouble().Should().Be(30);
        body.GetProperty("services").EnumerateArray().Single(s => s.GetProperty("service").GetString() == "menu")
            .GetProperty("traffic").GetProperty("requests").GetInt64().Should().Be(0);
        body.GetProperty("gatewayUptimeMinutes").GetDouble().Should().BeGreaterThanOrEqualTo(0);
    }

    // ------------------------------------------------------------------ traffic counters
    [Fact]
    public void The_counter_reports_volume_errors_average_p95_and_max()
    {
        var counter = new ServiceCounter();
        for (var i = 1; i <= 100; i++)
        {
            counter.Record(i <= 4 ? 502 : 200, i); // latencies 1..100 ms, four 5xx
        }

        var traffic = counter.Snapshot();

        traffic.Requests.Should().Be(100);
        traffic.ServerErrors.Should().Be(4);
        traffic.ErrorRate.Should().Be(0.04);
        traffic.AverageLatencyMs.Should().Be(50.5);
        traffic.P95LatencyMs.Should().Be(95);
        traffic.MaxLatencyMs.Should().Be(100);
    }

    [Fact]
    public void Client_errors_are_not_counted_as_server_errors_and_an_empty_counter_is_all_zero()
    {
        var counter = new ServiceCounter();
        counter.Snapshot().Should().Be(new ServiceTraffic(0, 0, 0, 0, 0, 0));

        counter.Record(404, 5);
        counter.Record(401, 5);
        counter.Record(499, 5);

        counter.Snapshot().ServerErrors.Should().Be(0);
    }

    [Fact]
    public void Only_the_most_recent_requests_shape_the_p95()
    {
        var counter = new ServiceCounter();
        for (var i = 0; i < 500; i++)
        {
            counter.Record(200, 1000); // an old slow period
        }

        for (var i = 0; i < 500; i++)
        {
            counter.Record(200, 10); // recovered
        }

        var traffic = counter.Snapshot();
        traffic.P95LatencyMs.Should().Be(10);
        traffic.MaxLatencyMs.Should().Be(1000); // the max still remembers it
    }

    [Fact]
    public async Task The_middleware_records_calls_to_known_services_and_ignores_everything_else()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ReverseProxy:Routes:menu-route:ClusterId"] = "menu",
            ["ReverseProxy:Routes:menu-route:Match:Path"] = "/api/v1/menu/{**catch-all}",
            ["ReverseProxy:Routes:tables-route:ClusterId"] = "table",
            ["ReverseProxy:Routes:tables-route:Match:Path"] = "/api/v1/tables/{**catch-all}",
        }).Build();
        var metrics = new GatewayMetrics();
        var middleware = new RequestMetricsMiddleware(ctx => { ctx.Response.StatusCode = StatusCodes.Status502BadGateway; return Task.CompletedTask; }, metrics, new RoutePrefixMap(config));

        await middleware.InvokeAsync(new DefaultHttpContext { Request = { Path = "/api/v1/tables/dining-tables" } });
        await middleware.InvokeAsync(new DefaultHttpContext { Request = { Path = "/api/v1/menu/products" } });
        await middleware.InvokeAsync(new DefaultHttpContext { Request = { Path = "/health/live" } });
        await middleware.InvokeAsync(new DefaultHttpContext { Request = { Path = "/api/v1/unknown/x" } });

        metrics.For("table").Should().BeEquivalentTo(new { Requests = 1L, ServerErrors = 1L });
        metrics.For("menu").Should().BeEquivalentTo(new { Requests = 1L, ServerErrors = 1L });
        metrics.For("unknown").Requests.Should().Be(0);
    }

    [Fact]
    public async Task A_failing_downstream_call_is_still_counted_when_it_throws()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ReverseProxy:Routes:menu-route:ClusterId"] = "menu",
            ["ReverseProxy:Routes:menu-route:Match:Path"] = "/api/v1/menu/{**catch-all}",
        }).Build();
        var metrics = new GatewayMetrics();
        var middleware = new RequestMetricsMiddleware(_ => throw new InvalidOperationException("boom"), metrics, new RoutePrefixMap(config));

        await FluentActions.Awaiting(() => middleware.InvokeAsync(new DefaultHttpContext { Request = { Path = "/api/v1/menu/products" } }))
            .Should().ThrowAsync<InvalidOperationException>();

        metrics.For("menu").Should().BeEquivalentTo(new { Requests = 1L, ServerErrors = 1L });
    }
}
