using AICOS.BuildingBlocks.Persistence;
using AICOS.Payments.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace AICOS.Payments.Api.Persistence;

public sealed class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options)
    : AicosDbContext(options)
{
    protected override string Schema => PaymentsService.Info.Schema;

    public DbSet<Bill> Bills => Set<Bill>();
    public DbSet<BillItem> BillItems => Set<BillItem>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Refund> Refunds => Set<Refund>();
}
