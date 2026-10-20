using System.Net;
using System.Net.Http.Headers;
using AICOS.BuildingBlocks.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AICOS.Services.SmokeTests;

public class GatewayTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public GatewayTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Health_is_live()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Invalid_bearer_token_is_rejected_at_the_edge()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-real-token");

        var response = await client.GetAsync("/api/v1/orders/_info");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Valid_bearer_token_passes_edge_validation()
    {
        var tokens = _factory.Services.GetRequiredService<JwtTokenService>();
        var (token, _) = tokens.CreateAccessToken(Guid.NewGuid(), "Manager A", AicosRoles.BranchManager, Guid.NewGuid(), Guid.NewGuid());

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/api/v1/orders/_info");

        // Downstream service is not running in this test (502/503/504), or is (200) - either way not an auth failure.
        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Correlation_id_is_generated_and_echoed()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/");

        response.Headers.GetValues("X-Correlation-Id").Single().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Client_supplied_correlation_id_is_preserved()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("X-Correlation-Id", "abc-123");

        var response = await client.SendAsync(request);

        response.Headers.GetValues("X-Correlation-Id").Single().Should().Be("abc-123");
    }
}
