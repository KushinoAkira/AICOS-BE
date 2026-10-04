using AICOS.BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.Ordering.Api.Persistence;

public sealed class OrderingDbContext(DbContextOptions<OrderingDbContext> options) : AicosDbContext(options)
{
    protected override string Schema => OrderingService.Info.Schema;

    // public DbSet<Something> Somethings => Set<Something>();
}