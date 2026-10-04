using AICOS.BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.IdentityOrganization.Api.Persistence;

public sealed class IdentityOrganizationDbContext(DbContextOptions<IdentityOrganizationDbContext> options) : AicosDbContext(options)
{
    protected override string Schema => IdentityOrganizationService.Info.Schema;

    // public DbSet<Something> Somethings => Set<Something>();
}