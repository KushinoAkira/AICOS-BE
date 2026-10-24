using AICOS.BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.MenuCatalog.Api.Persistence;

public sealed class MenuCatalogDbContext(DbContextOptions<MenuCatalogDbContext> options) : AicosDbContext(options)
{
    protected override string Schema => MenuCatalogService.Info.Schema;

    // public DbSet<Something> Somethings => Set<Something>();
}