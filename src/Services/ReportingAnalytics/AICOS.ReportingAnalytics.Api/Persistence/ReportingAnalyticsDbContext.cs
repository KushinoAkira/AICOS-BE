using AICOS.BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.ReportingAnalytics.Api.Persistence;

public sealed class ReportingAnalyticsDbContext(DbContextOptions<ReportingAnalyticsDbContext> options) : AicosDbContext(options)
{
    protected override string Schema => ReportingAnalyticsService.Info.Schema;

    // public DbSet<Something> Somethings => Set<Something>();
}