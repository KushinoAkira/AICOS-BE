using AICOS.BuildingBlocks.Persistence;
using AICOS.Ordering.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace AICOS.Ordering.Api.Persistence;

public sealed class OrderingDbContext(DbContextOptions<OrderingDbContext> options) : AicosDbContext(options)
{
    protected override string Schema => OrderingService.Info.Schema;

    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<OrderItemOption> OrderItemOptions => Set<OrderItemOption>();
    public DbSet<OrderItemEvent> OrderItemEvents => Set<OrderItemEvent>();
}
