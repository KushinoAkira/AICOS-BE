using AICOS.BuildingBlocks.Persistence;
using AICOS.MenuCatalog.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace AICOS.MenuCatalog.Api.Persistence;

public sealed class MenuCatalogDbContext(DbContextOptions<MenuCatalogDbContext> options) : AicosDbContext(options)
{
    protected override string Schema => MenuCatalogService.Info.Schema;

    public DbSet<Category> Categories => Set<Category>();

    public DbSet<Product> Products => Set<Product>();

    public DbSet<Promotion> Promotions => Set<Promotion>();
}
