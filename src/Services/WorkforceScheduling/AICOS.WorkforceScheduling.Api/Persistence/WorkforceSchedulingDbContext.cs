using AICOS.BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.WorkforceScheduling.Api.Persistence;

public sealed class WorkforceSchedulingDbContext(DbContextOptions<WorkforceSchedulingDbContext> options) : AicosDbContext(options)
{
    protected override string Schema => WorkforceSchedulingService.Info.Schema;

    // public DbSet<Something> Somethings => Set<Something>();
}
