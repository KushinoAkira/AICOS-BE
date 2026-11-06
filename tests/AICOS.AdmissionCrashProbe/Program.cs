using System.Data.Common;
using System.Text.Json;
using AICOS.BuildingBlocks.Domain;
using AICOS.Contracts.KitchenAdmission;
using AICOS.KitchenOperations.Api.Features.KitchenAdmission;
using AICOS.KitchenOperations.Api.Persistence;
using AICOS.Ordering.Api.Domain.Entities;
using AICOS.Ordering.Api.Domain.Enums;
using AICOS.Ordering.Api.Persistence;
using AICOS.Services.IntegrationTests;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

var mode = args.Single();
var connection = OperationalTestTarget.Required(mode == "ordering-after-commit" ? "AICOS_OPERATIONS_ORDERING_CONNECTION" : "AICOS_OPERATIONS_KITCHEN_CONNECTION");
await OperationalTestTarget.VerifyAsync(connection);
var input = await Console.In.ReadToEndAsync();
var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{ ["KitchenAdmission:CaptureEnabled"] = "true", ["KitchenAdmission:ReceiverEnabled"] = "true" }).Build();
if (mode == "ordering-after-commit")
{
    var ids = JsonSerializer.Deserialize<Guid[]>(input)!;
    await using var db = new OrderingDbContext(new DbContextOptionsBuilder<OrderingDbContext>().UseNpgsql(connection)
        .UseSnakeCaseNamingConvention().Options, configuration);
    var order = new Order(ids[0], ids[1], OrderType.DineIn, DateTime.UtcNow) { CreatedAtUtc = DateTime.UtcNow };
    order.AddItem(ids[2], "Crash probe coffee", 50000, 1);
    db.Orders.Add(order); await db.SaveChangesAsync();
    await Boundary(order.Id);
}
else
{
    var request = JsonSerializer.Deserialize<AdmissionRequest>(input, AdmissionProtocol.Json)!;
    await using var db = new KitchenOperationsDbContext(new DbContextOptionsBuilder<KitchenOperationsDbContext>()
        .UseNpgsql(connection).UseSnakeCaseNamingConvention().AddInterceptors(new SaveBoundary(mode, request.OrderId), new CommitBoundary(mode, request.OrderId)).Options);
    var admission = new KitchenAdmissionService(db, new RecordingNotifier(), TimeProvider.System, configuration, NullLogger<KitchenAdmissionService>.Instance);
    await admission.EnsureAsync(request, true, default);
    throw new InvalidOperationException("The intended crash boundary was not reached.");
}

static async Task Boundary(Guid order)
{
    Console.WriteLine("READY:" + order); await Console.Out.FlushAsync();
    await Task.Delay(Timeout.Infinite);
}

sealed class SaveBoundary(string mode, Guid order) : SaveChangesInterceptor
{
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
    {
        foreach (var entry in data.Context!.ChangeTracker.Entries<IAuditable>().Where(e => e.State == EntityState.Added)) entry.Entity.CreatedAtUtc = DateTime.UtcNow;
        if (mode == "kitchen-before-commit")
        {
            Console.WriteLine("READY:" + order); await Console.Out.FlushAsync(); await Task.Delay(Timeout.Infinite, ct);
        }
        return result;
    }
}

sealed class CommitBoundary(string mode, Guid order) : DbTransactionInterceptor
{
    public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData data, CancellationToken ct = default)
    {
        if (mode == "kitchen-after-commit")
        {
            Console.WriteLine("READY:" + order); await Console.Out.FlushAsync(); await Task.Delay(Timeout.Infinite, ct);
        }
    }
}
