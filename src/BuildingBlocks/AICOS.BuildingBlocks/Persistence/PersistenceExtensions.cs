using AICOS.BuildingBlocks.Hosting;
using Microsoft.EntityFrameworkCore;

namespace AICOS.BuildingBlocks.Persistence;

public static class PersistenceExtensions
{
    /// <summary>
    /// Registers the service's DbContext on PostgreSQL: snake_case naming, migrations history table inside the
    /// service's own schema, audit interceptor, retry-on-failure and a readiness health check.
    /// Reads <c>ConnectionStrings:Default</c>; when it carries no password, <c>Database:Password</c> (user-secrets / env) is added, so no password lives in a committed file.
    /// </summary>
    public static WebApplicationBuilder AddAicosPostgres<TContext>(this WebApplicationBuilder builder, AicosServiceInfo info)
        where TContext : DbContext
    {
        var connectionString = ResolveConnectionString(builder.Configuration);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"ConnectionStrings:Default is not configured for service '{info.Name}'. " +
                "Set ConnectionStrings__Default (compose does), or a connection string without Password plus Database__Password (user-secrets or env).");
        }

        builder.Services.AddScoped<AuditableEntityInterceptor>();
        builder.Services.AddDbContext<TContext>((sp, options) => options
            .UseNpgsql(connectionString, npgsql => npgsql
                .MigrationsHistoryTable("__ef_migrations_history", info.Schema)
                .EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(5), errorCodesToAdd: null))
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(sp.GetRequiredService<AuditableEntityInterceptor>()));

        builder.Services.AddHealthChecks()
            .AddNpgSql(connectionString, name: "postgres", tags: ["ready"]);

        return builder;
    }

    /// <summary>The connection string with the password filled in from <c>Database:Password</c> when the string has none.</summary>
    public static string? ResolveConnectionString(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Default");
        var password = configuration["Database:Password"];
        if (string.IsNullOrWhiteSpace(connectionString) || string.IsNullOrEmpty(password))
        {
            return connectionString;
        }

        var parsed = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrEmpty(parsed.Password))
        {
            parsed.Password = password;
        }

        return parsed.ConnectionString;
    }

    /// <summary>
    /// Applies pending EF Core migrations when <c>Database:MigrateOnStartup</c> is true (dev / docker-compose).
    /// In production prefer running migrations as a separate deployment step.
    /// </summary>
    public static async Task MigrateDatabaseAsync<TContext>(this WebApplication app)
        where TContext : DbContext
    {
        if (!app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
        {
            return;
        }

        await using var scope = app.Services.CreateAsyncScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Database");
        var context = scope.ServiceProvider.GetRequiredService<TContext>();

        logger.LogInformation("Applying database migrations for {Context}...", typeof(TContext).Name);
        await context.Database.MigrateAsync();
    }
}
