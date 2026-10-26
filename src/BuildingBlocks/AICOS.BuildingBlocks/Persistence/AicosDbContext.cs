using Microsoft.EntityFrameworkCore;

namespace AICOS.BuildingBlocks.Persistence;

/// <summary>
/// Base DbContext for every .NET service. Uses the configured operational schema and auto-loads all
/// <c>IEntityTypeConfiguration&lt;T&gt;</c> from the service assembly. AICOS currently standardizes on
/// one database and the <c>public</c> schema; table ownership is enforced with PostgreSQL grants.
/// </summary>
public abstract class AicosDbContext(DbContextOptions options) : DbContext(options)
{
    /// <summary>Operational schema used by this service. Currently <c>public</c> for every service.</summary>
    protected abstract string Schema { get; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(GetType().Assembly);
        base.OnModelCreating(modelBuilder);
    }
}
