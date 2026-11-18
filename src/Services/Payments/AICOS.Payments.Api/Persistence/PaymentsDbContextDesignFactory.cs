using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
namespace AICOS.Payments.Api.Persistence;
/// <summary>Offline migration scaffolding only. No host, credentials, seed or live connection.</summary>
public sealed class PaymentsDbContextDesignFactory : IDesignTimeDbContextFactory<PaymentsDbContext>
{
    public PaymentsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<PaymentsDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=aicos_offline_schema;Username=design;Timeout=1", p => p.MigrationsHistoryTable("__ef_migrations_history", "payment"))
            .UseSnakeCaseNamingConvention().Options;
        return new PaymentsDbContext(options);
    }
}
