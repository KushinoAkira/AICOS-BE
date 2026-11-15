using System.Data.Common;
using AICOS.Contracts.Events;
using AICOS.Ordering.Api.Features.Orders;
using AICOS.Ordering.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
namespace AICOS.Services.IntegrationTests;

[Collection("Session bill PostgreSQL")]
public sealed class SessionBillProjectionPostgresTests : IAsyncLifetime
{
    private readonly SessionBillHarness _h = new();
    public Task InitializeAsync() => _h.InitializeAsync(Environment.GetEnvironmentVariable("AICOS_BILL_TEST_CONNECTION")!);
    public Task DisposeAsync() { _h.Dispose(); return Task.CompletedTask; }

    private sealed class FirstOrderReadGate : DbCommandInterceptor
    {
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reads;
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData data, DbDataReader result, CancellationToken ct = default)
        {
            if (command.CommandText.Contains("ordering.orders") && Interlocked.Increment(ref _reads) == 1)
            {
                Entered.TrySetResult(true);
                await Release.Task.WaitAsync(ct);
            }
            return result;
        }
    }

    [BillPostgresFact]
    public async Task Concurrent_started_and_ready_events_never_regress_a_paid_order()
    {
        var order = await _h.RoundAsync();
        await _h.CashAsync(await _h.BillAsync());
        var gate = new FirstOrderReadGate();
        var connection = Environment.GetEnvironmentVariable("AICOS_BILL_TEST_CONNECTION")!;
        var config = new ConfigurationBuilder().AddInMemoryCollection(_h.Settings).Build();
        DbContextOptionsBuilder<OrderingDbContext> Options() => new DbContextOptionsBuilder<OrderingDbContext>().UseNpgsql(connection).UseSnakeCaseNamingConvention();
        await using var startedDb = new OrderingDbContext(Options().AddInterceptors(gate).Options, config);
        await using var readyDb = new OrderingDbContext(Options().Options, config);
        using var scope = _h.Ordering.Services.CreateScope();
        var events = scope.ServiceProvider.GetRequiredService<OrderEventPublisher>();
        var started = new KitchenOrderStartedHandler(startedDb, events).HandleAsync(new KitchenOrderStartedIntegrationEvent(_h.Tenant, Guid.NewGuid(), order, _h.Branch, DateTime.UtcNow), default);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var ready = new KitchenOrderReadyHandler(readyDb, events).HandleAsync(new KitchenOrderReadyIntegrationEvent(_h.Tenant, Guid.NewGuid(), order, "test", _h.Branch, "S01", DateTime.UtcNow), default);
        await Task.WhenAny(ready, Task.Delay(500));
        gate.Release.TrySetResult(true);
        await Task.WhenAll(started, ready);
        readyDb.ChangeTracker.Clear();
        var saved = await readyDb.Orders.SingleAsync(o => o.Id == order);
        saved.Status.Should().Be(AICOS.Ordering.Api.Domain.Enums.OrderStatus.Ready);
        saved.ReadyAtUtc.Should().NotBeNull();
        saved.IsPaid.Should().BeTrue();
    }
}
