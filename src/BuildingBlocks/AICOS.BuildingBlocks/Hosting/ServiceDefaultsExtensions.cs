using System.Text.Json.Serialization;
using AICOS.BuildingBlocks.Http;
using AICOS.BuildingBlocks.Security;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Routing;
using Microsoft.OpenApi.Models;
using Serilog;

namespace AICOS.BuildingBlocks.Hosting;

/// <summary>
/// One-liner bootstrapping shared by every microservice, so each Program.cs stays tiny and all services behave the same
/// (logging, error format, auth, health checks, Swagger).
/// </summary>
public static class ServiceDefaultsExtensions
{
    /// <summary>Full defaults for a domain service.</summary>
    public static WebApplicationBuilder AddAicosServiceDefaults(this WebApplicationBuilder builder, AicosServiceInfo info)
    {
        builder.Services.AddSingleton(info);

        builder.AddAicosLogging(info.Name);
        builder.Services.AddAicosCore();
        builder.Services.AddAicosAuthentication(builder.Configuration);
        builder.Services.AddAicosSwagger(info);
        builder.Services.AddHealthChecks();

        return builder;
    }

    /// <summary>Serilog console logging enriched with the service name and correlation id.</summary>
    public static WebApplicationBuilder AddAicosLogging(this WebApplicationBuilder builder, string serviceName)
    {
        builder.Host.UseSerilog((context, services, logger) => logger
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Service", serviceName)
            .WriteTo.Console(outputTemplate:
                "[{Timestamp:HH:mm:ss} {Level:u3}] [{Service}] [{CorrelationId}] {Message:lj}{NewLine}{Exception}"));

        return builder;
    }

    /// <summary>Clock, current user, ProblemDetails + global exception handler, JSON conventions.</summary>
    public static IServiceCollection AddAicosCore(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, CurrentUser>();

        services.AddProblemDetails();
        services.AddExceptionHandler<GlobalExceptionHandler>();

        services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        return services;
    }

    public static IServiceCollection AddAicosSwagger(this IServiceCollection services, AicosServiceInfo info)
    {
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo { Title = $"AICOS {info.DisplayName}", Version = "v1" });
            options.CustomSchemaIds(type => type.FullName?.Replace('+', '.'));

            var bearer = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Description = "Paste the JWT access token issued by the Identity service.",
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" },
            };
            options.AddSecurityDefinition("Bearer", bearer);
            options.AddSecurityRequirement(new OpenApiSecurityRequirement { [bearer] = [] });
        });

        return services;
    }

    /// <summary>Middleware pipeline shared by all domain services (order matters).</summary>
    public static WebApplication UseAicosServiceDefaults(this WebApplication app)
    {
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseSerilogRequestLogging();
        app.UseExceptionHandler();
        app.UseStatusCodePages();

        if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Swagger:Enabled"))
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        app.UseAuthentication();
        app.UseAuthorization();

        app.MapAicosHealthChecks();
        return app;
    }

    /// <summary><c>/health/live</c> = process is up; <c>/health/ready</c> = dependencies (PostgreSQL, Redis) are reachable.</summary>
    public static IEndpointRouteBuilder MapAicosHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
        return endpoints;
    }

    /// <summary>
    /// Creates the service's versioned API group <c>/api/v1/{RoutePrefix}</c> (same prefix the gateway routes on)
    /// and maps <c>GET _info</c>. Add your endpoints to the returned group.
    /// </summary>
    public static RouteGroupBuilder MapAicosApiGroup(this WebApplication app)
    {
        var info = app.Services.GetRequiredService<AicosServiceInfo>();
        var group = app.MapGroup(info.ApiBasePath).WithTags(info.DisplayName);

        group.MapGet("/_info", (IHostEnvironment environment, TimeProvider clock) => new
        {
            service = info.Name,
            displayName = info.DisplayName,
            schema = info.Schema,
            environment = environment.EnvironmentName,
            utcNow = clock.GetUtcNow(),
        }).AllowAnonymous();

        return group;
    }
}
