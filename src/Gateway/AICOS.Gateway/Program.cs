using System.Threading.RateLimiting;
using AICOS.BuildingBlocks.Hosting;
using AICOS.BuildingBlocks.Http;
using AICOS.Gateway;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.AddAicosLogging("gateway");
builder.Services.AddAicosCore();
builder.Services.AddAicosAuthentication(builder.Configuration);
builder.Services.AddHealthChecks();
builder.Services.AddSingleton<GatewayMetrics>();
builder.Services.AddSingleton<RoutePrefixMap>();
builder.Services.AddHttpClient(PlatformHealthEndpoints.HttpClientName);

// CORS for the React dashboard and Flutter web builds.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
{
    if (allowedOrigins.Length > 0)
    {
        policy.WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .WithExposedHeaders(CorrelationIdMiddleware.HeaderName);
    }
}));

// Per-client-IP rate limiting at the edge. Sign-in, sign-up, password reset and chatbot calls get a much smaller budget than normal
// traffic (NFR-11); each of those groups counts separately so a busy chat never locks a user out of signing in.
var permitLimit = builder.Configuration.GetValue("RateLimiting:PermitLimitPerMinute", 600);
var authLimit = builder.Configuration.GetValue("RateLimiting:AuthPermitLimitPerMinute", 10);
var chatLimit = builder.Configuration.GetValue("RateLimiting:ChatPermitLimitPerMinute", 20);
var qrLimit = builder.Configuration.GetValue("RateLimiting:QrPermitLimitPerMinute", 120);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = (context, _) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "60";
        return ValueTask.CompletedTask;
    };
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var (group, limit) = RateLimitGroups.Classify(context.Request.Method, context.Request.Path, permitLimit, authLimit, chatLimit, qrLimit);
        return RateLimitPartition.GetFixedWindowLimiter(
            $"{group}:{ip}",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = limit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
    });
});

builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseSerilogRequestLogging();
app.UseExceptionHandler();
app.UseMiddleware<RequestMetricsMiddleware>();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<BearerTokenValidationMiddleware>();
app.UseAuthorization();

app.MapGet("/", () => Results.Ok(new { service = "gateway", name = "AICOS API Gateway" }));
app.MapHealthChecks("/health/live");
app.MapPlatformHealth();
app.MapReverseProxy();

await app.RunAsync();

public partial class Program;
