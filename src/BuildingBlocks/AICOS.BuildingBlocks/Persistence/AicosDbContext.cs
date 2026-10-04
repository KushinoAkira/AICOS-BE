using Microsoft.EntityFrameworkCore;

namespace AICOS.BuildingBlocks.Persistence;

/// <summary>
/// Base DbContext for every .NET service. Pins the context to the service's own PostgreSQL schema
/// (schema-per-service isolation) and auto-loads all <c>IEntityTypeConfiguration&lt;T&gt;</c> from the service assembly.
/// </summary>
public abstract class AicosDbContext(DbContextOptions options) : DbContext(options)
{
    /// <summary>Schema owned by this service, e.g. "ordering". Must match infra/postgres/init.</summary>
    protected abstract string Schema { get; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(GetType().Assembly);
        base.OnModelCreating(modelBuilder);
    }
}
