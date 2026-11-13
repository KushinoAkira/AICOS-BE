using AICOS.BuildingBlocks.Exceptions;
using AICOS.TableReservation.Api.Domain;
using AICOS.TableReservation.Api.Features.DiningTables;
using Microsoft.Extensions.Configuration;
namespace AICOS.Services.IntegrationTests;

public sealed class SessionBillOccupancyTests
{
    private sealed class Sessions : ITableOccupancyClient
    {
        public HashSet<Guid> Active { get; } = [];
        public bool Unavailable { get; set; }
        public int Calls { get; private set; }
        public Task<IReadOnlySet<Guid>> ActiveAsync(Guid tenant, CancellationToken ct)
        {
            Calls++;
            if (Unavailable) throw new DependencyUnavailableException("ordering-occupancy");
            return Task.FromResult<IReadOnlySet<Guid>>(Active);
        }
    }
    private static DiningTableResponse Row(TableStatus status) => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Inside", "S01", 4, null, null, status, "token", "qr", DateTime.UtcNow);
    private static TableOccupancyProjection Projection(Sessions sessions, bool enabled) => new(sessions, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { { "SessionBilling:Enabled", enabled.ToString() } }).Build());

    [Fact]
    public async Task Authoritative_sessions_clear_closed_tables_and_preserve_out_of_service()
    {
        var sessions = new Sessions(); var closed = Row(TableStatus.Occupied); var active = Row(TableStatus.Available); var disabled = Row(TableStatus.OutOfService);
        sessions.Active.UnionWith([active.Id, disabled.Id]);
        var rows = await Projection(sessions, true).ApplyAsync(Guid.NewGuid(), [closed, active, disabled], default);
        rows.Select(r => r.Status).Should().Equal(TableStatus.Available, TableStatus.Occupied, TableStatus.OutOfService);
        rows.Select(r => r.Id).Should().Equal(closed.Id, active.Id, disabled.Id);
    }
    [Fact]
    public async Task Feature_off_preserves_legacy_and_does_not_contact_ordering()
    {
        var sessions = new Sessions { Unavailable = true }; var table = Row(TableStatus.Occupied);
        (await Projection(sessions, false).ApplyAsync(Guid.NewGuid(), [table], default)).Should().Equal(table);
        sessions.Calls.Should().Be(0);
    }
    [Fact]
    public async Task Dependency_failure_is_not_presented_as_an_available_table()
    {
        var sessions = new Sessions { Unavailable = true };
        Func<Task> read = () => Projection(sessions, true).ApplyAsync(Guid.NewGuid(), [Row(TableStatus.Occupied)], default);
        await read.Should().ThrowAsync<DependencyUnavailableException>();
    }
}
