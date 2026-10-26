using AICOS.BuildingBlocks.Hosting;
using Microsoft.EntityFrameworkCore;

namespace AICOS.BuildingBlocks.Persistence;

public static class PersistenceExtensions
{
    /// <summary>
    /// Registers the service's DbContext on PostgreSQL: snake_case naming, migrations history table inside the
    /// configured schema, audit interceptor, retry-on-failure and a readiness health check.
    /// Reads <c>ConnectionStrings:Default</c>.
    /// </summary>
    public static WebApplicationBuilder AddAicosPostgres<TContext>(this WebApplicationBuilder builder, AicosServiceInfo info)
        where TContext : DbContext
    {
        var connectionString = builder.Configuration.GetConnectionString("Default");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"ConnectionStrings:Default is not configured for service '{info.Name}'. " +
                "Set it in appsettings.Development.json or via env var ConnectionStrings__Default.");
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

    /// <summary>
    /// Applies pending EF Core migrations only when <c>Database:MigrateOnStartup</c> is explicitly enabled.
    /// Checked-in local and Docker settings keep it disabled; use a reviewed deployment step instead.
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
