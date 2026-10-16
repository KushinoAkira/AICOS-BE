using System.Net.Http.Headers;

namespace AICOS.BuildingBlocks.Http;

/// <summary>Forwards the caller's correlation id and bearer token on service-to-service REST calls.</summary>
internal sealed class ServiceCallHandler(IHttpContextAccessor accessor) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var context = accessor.HttpContext;
        if (context is not null)
        {
            if (context.Items[CorrelationIdMiddleware.HeaderName] is string correlationId)
            {
                request.Headers.TryAddWithoutValidation(CorrelationIdMiddleware.HeaderName, correlationId);
            }

            if (request.Headers.Authorization is null
                && AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization, out var authorization))
            {
                request.Headers.Authorization = authorization;
            }
        }

        return base.SendAsync(request, cancellationToken);
    }
}

public static class ServiceClientExtensions
{
    /// <summary>
    /// Registers a typed HttpClient for a synchronous call to another microservice.
    /// Base URL comes from <c>Services:{targetService}:BaseUrl</c>. Includes standard resilience (retry, circuit breaker, timeouts)
    /// and correlation-id / bearer-token propagation. Calls go service-to-service directly, NOT through the gateway.
    /// </summary>
    public static IHttpClientBuilder AddAicosServiceClient<TClient, TImplementation>(
        this IServiceCollection services,
        IConfiguration configuration,
        string targetService,
        Action<Microsoft.Extensions.Http.Resilience.HttpStandardResilienceOptions>? configureResilience = null)
        where TClient : class
        where TImplementation : class, TClient
    {
        var baseUrl = configuration[$"Services:{targetService}:BaseUrl"]
            ?? throw new InvalidOperationException($"Services:{targetService}:BaseUrl is not configured.");

        services.AddHttpContextAccessor();
        services.AddTransient<ServiceCallHandler>();

        var builder = services
            .AddHttpClient<TClient, TImplementation>(client => client.BaseAddress = new Uri(baseUrl))
            .AddHttpMessageHandler<ServiceCallHandler>();

        builder.AddStandardResilienceHandler(options => configureResilience?.Invoke(options));
        return builder;
    }
}
