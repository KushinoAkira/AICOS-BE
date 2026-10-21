using System.Net;
using System.Net.Http.Json;
using AICOS.Gateway;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AICOS.Services.SmokeTests;

/// <summary>SCRUM-143 / NFR-11: sign-in, password reset and chatbot calls have a small budget of their own and answer 429 beyond it.</summary>
public class RateLimitTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private WebApplicationFactory<Program> Gateway(int auth = 3, int chat = 2) => factory.WithWebHostBuilder(builder =>
    {
        builder.UseSetting("RateLimiting:AuthPermitLimitPerMinute", auth.ToString());
        builder.UseSetting("RateLimiting:ChatPermitLimitPerMinute", chat.ToString());
    });

    private static async Task<List<HttpStatusCode>> PostManyAsync(HttpClient client, string path, int times)
    {
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < times; i++)
        {
            codes.Add((await client.PostAsJsonAsync(path, new { })).StatusCode);
        }

        return codes;
    }

    [Theory]
    [InlineData("/api/v1/identity/auth/login")]
    [InlineData("/api/v1/identity/auth/forgot-password")]
    [InlineData("/api/v1/identity/auth/reset-password")]
    [InlineData("/api/v1/identity/auth/register")]
    public async Task Auth_endpoints_answer_429_after_their_own_small_budget(string path)
    {
        using var client = Gateway().CreateClient();

        var codes = await PostManyAsync(client, path, 5);

        codes.Take(3).Should().NotContain(HttpStatusCode.TooManyRequests);       // the identity service is not running here: any other answer will do
        codes.Skip(3).Should().OnlyContain(c => c == HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task A_rejected_call_says_when_to_retry()
    {
        using var client = Gateway(auth: 1).CreateClient();
        await client.PostAsJsonAsync("/api/v1/identity/auth/login", new { });

        var response = await client.PostAsJsonAsync("/api/v1/identity/auth/login", new { });

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task Chat_has_its_own_budget_and_never_uses_up_the_sign_in_budget()
    {
        using var client = Gateway().CreateClient();

        var chat = await PostManyAsync(client, "/api/v1/ai/chat", 4);
        var login = await PostManyAsync(client, "/api/v1/identity/auth/login", 3);

        chat.Skip(2).Should().OnlyContain(c => c == HttpStatusCode.TooManyRequests);
        login.Should().NotContain(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Everything_else_keeps_the_normal_budget()
    {
        using var client = Gateway().CreateClient();

        for (var i = 0; i < 30; i++)
        {
            (await client.GetAsync("/health/live")).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        (await client.GetAsync("/api/v1/identity/auth/login")).StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);   // only POST is limited as sign-in
    }

    [Theory]
    [InlineData("POST", "/api/v1/identity/auth/login", "auth", 10)]
    [InlineData("post", "/API/V1/IDENTITY/AUTH/LOGIN", "auth", 10)]
    [InlineData("GET", "/api/v1/identity/auth/login", "all", 600)]
    [InlineData("POST", "/api/v1/ai/chat/messages", "chat", 20)]
    [InlineData("POST", "/api/v1/orders", "all", 600)]
    public void Requests_are_classified_into_budgets(string method, string path, string group, int limit) =>
        RateLimitGroups.Classify(method, path, 600, 10, 20).Should().Be((group, limit));
}
