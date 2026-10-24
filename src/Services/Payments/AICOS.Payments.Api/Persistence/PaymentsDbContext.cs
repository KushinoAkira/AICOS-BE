using AICOS.BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.Payments.Api.Persistence;

public sealed class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : AicosDbContext(options)
{
    protected override string Schema => PaymentsService.Info.Schema;

    // public DbSet<Something> Somethings => Set<Something>();
}