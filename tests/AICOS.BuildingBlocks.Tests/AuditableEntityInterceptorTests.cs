using AICOS.BuildingBlocks.Domain;
using AICOS.BuildingBlocks.Persistence;
using AICOS.BuildingBlocks.Security;
using Microsoft.EntityFrameworkCore;

namespace AICOS.BuildingBlocks.Tests;

public class AuditableEntityInterceptorTests
{
    private sealed class Widget : AuditableEntity<Guid>
    {
        public Widget() => Id = Guid.NewGuid();

        public string Name { get; set; } = string.Empty;
    }

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        public DbSet<Widget> Widgets => Set<Widget>();
    }

    private sealed class FakeUser(Guid? id) : ICurrentUser
    {
        public bool IsAuthenticated => id is not null;
        public Guid? UserId => id;
        public string? Name => "tester";
        public string? Role => AicosRoles.Manager;
        public Guid? BranchId => null;
        public bool IsInRole(string role) => role == Role;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static TestDbContext CreateContext(Guid? userId, DateTimeOffset now)
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(new AuditableEntityInterceptor(new FakeUser(userId), new FixedClock(now)))
            .Options;
        return new TestDbContext(options);
    }

    [Fact]
    public async Task Added_entities_get_created_stamps()
    {
        var userId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        await using var db = CreateContext(userId, now);

        var widget = new Widget { Name = "a" };
        db.Widgets.Add(widget);
        await db.SaveChangesAsync();

        widget.CreatedAtUtc.Should().Be(now.UtcDateTime);
        widget.CreatedBy.Should().Be(userId);
        widget.UpdatedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task Modified_entities_get_updated_stamps_and_keep_created_stamps()
    {
        var userId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        await using var db = CreateContext(userId, now);

        var widget = new Widget { Name = "a" };
        db.Widgets.Add(widget);
        await db.SaveChangesAsync();

        widget.Name = "b";
        await db.SaveChangesAsync();

        widget.UpdatedAtUtc.Should().Be(now.UtcDateTime);
        widget.UpdatedBy.Should().Be(userId);
        widget.CreatedAtUtc.Should().Be(now.UtcDateTime);
    }

    [Fact]
    public async Task Anonymous_writes_leave_user_null()
    {
        await using var db = CreateContext(userId: null, DateTimeOffset.UtcNow);

        var widget = new Widget { Name = "a" };
        db.Widgets.Add(widget);
        await db.SaveChangesAsync();

        widget.CreatedBy.Should().BeNull();
        widget.CreatedAtUtc.Should().NotBe(default);
    }
}
