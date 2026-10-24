using AICOS.BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.CustomerEngagement.Api.Persistence;

public sealed class CustomerEngagementDbContext(DbContextOptions<CustomerEngagementDbContext> options) : AicosDbContext(options)
{
    protected override string Schema => CustomerEngagementService.Info.Schema;

    // public DbSet<Something> Somethings => Set<Something>();
}