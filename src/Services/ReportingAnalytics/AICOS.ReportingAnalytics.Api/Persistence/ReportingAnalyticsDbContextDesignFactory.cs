using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
namespace AICOS.ReportingAnalytics.Api.Persistence;
/// <summary>Offline migration scaffolding; cannot reach any real database.</summary>
public sealed class ReportingAnalyticsDbContextDesignFactory:IDesignTimeDbContextFactory<ReportingAnalyticsDbContext>
{
    public ReportingAnalyticsDbContext CreateDbContext(string[] args)=>new(new DbContextOptionsBuilder<ReportingAnalyticsDbContext>().UseNpgsql("Host=127.0.0.1;Port=1;Database=aicos_offline_schema;Username=design;Timeout=1",p=>p.MigrationsHistoryTable("__ef_migrations_history","reporting")).UseSnakeCaseNamingConvention().Options);
}
