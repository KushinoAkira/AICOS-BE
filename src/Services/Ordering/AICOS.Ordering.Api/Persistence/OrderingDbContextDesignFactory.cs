using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
namespace AICOS.Ordering.Api.Persistence;
/// <summary>Offline migration scaffolding only. No host, credentials, seed or live connection.</summary>
public sealed class OrderingDbContextDesignFactory : IDesignTimeDbContextFactory<OrderingDbContext>
{
    public OrderingDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<OrderingDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=aicos_offline_schema;Username=design;Timeout=1", p => p.MigrationsHistoryTable("__ef_migrations_history", "ordering"))
            .UseSnakeCaseNamingConvention().Options;
        return new OrderingDbContext(options);
    }
}
