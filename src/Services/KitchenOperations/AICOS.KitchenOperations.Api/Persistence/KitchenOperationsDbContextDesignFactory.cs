using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
namespace AICOS.KitchenOperations.Api.Persistence;
public sealed class KitchenOperationsDbContextDesignFactory : IDesignTimeDbContextFactory<KitchenOperationsDbContext>
{
    public KitchenOperationsDbContext CreateDbContext(string[] args)=>new(new DbContextOptionsBuilder<KitchenOperationsDbContext>().UseNpgsql("Host=127.0.0.1;Port=1;Database=offline;Username=design;Timeout=1",p=>p.MigrationsHistoryTable("__ef_migrations_history","kitchen")).UseSnakeCaseNamingConvention().Options);
}
