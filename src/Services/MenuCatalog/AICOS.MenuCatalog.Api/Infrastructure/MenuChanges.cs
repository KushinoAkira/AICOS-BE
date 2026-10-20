using System.Text.Json;
using AICOS.BuildingBlocks.Caching;
using AICOS.BuildingBlocks.Messaging;
using AICOS.Contracts.Events;
using AICOS.MenuCatalog.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.MenuCatalog.Api.Infrastructure;

/// <summary>
/// The published menu of a branch is read far more often than it changes, so it is cached in Redis (UC-15, NFR-25).
/// Each branch has a version counter; bumping it makes the cached copies obsolete at once, with no key scanning.
/// </summary>
public sealed class MenuCache(ICacheStore store)
{
    private static readonly TimeSpan TimeToLive = TimeSpan.FromMinutes(10); // safety net; changes invalidate immediately
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<T> GetOrBuildAsync<T>(Guid branchId, string view, Func<Task<T>> build, CancellationToken ct)
    {
        var key = await KeyAsync(branchId, view, ct);
        var cached = await store.GetAsync(key, ct);
        if (cached is not null)
        {
            var hit = JsonSerializer.Deserialize<T>(cached, Json);
            if (hit is not null)
            {
                return hit;
            }
        }

        var value = await build();
        await store.SetAsync(key, JsonSerializer.Serialize(value, Json), TimeToLive, ct);
        return value;
    }

    public Task InvalidateBranchAsync(Guid branchId, CancellationToken ct) => store.IncrementAsync(VersionKey(branchId), ct);

    private async Task<string> KeyAsync(Guid branchId, string view, CancellationToken ct) =>
        $"menu:{view}:{branchId}:v{await store.GetCounterAsync(VersionKey(branchId), ct)}";

    private static string VersionKey(Guid branchId) => $"menu:version:{branchId}";
}

/// <summary>
/// Called after every change that alters what a branch can sell: clears the cached menus and tells the other services
/// (QR ordering, counter POS, takeaway) with a <see cref="MenuChangedEvent"/>. A failure to publish never fails the change.
/// </summary>
public sealed class MenuChangeNotifier(MenuCache cache, MenuCatalogDbContext db, IEventBus bus, ILogger<MenuChangeNotifier> logger)
{
    /// <summary>One branch changed (publication, price, sold out).</summary>
    public async Task BranchChangedAsync(Guid tenantId, Guid branchId, string reason, CancellationToken ct)
    {
        await cache.InvalidateBranchAsync(branchId, ct);
        await PublishAsync(tenantId, branchId, reason, ct);
    }

    /// <summary>The brand-level catalogue changed (category, product, options): every branch that sells it is affected.</summary>
    public async Task TenantChangedAsync(Guid tenantId, string reason, CancellationToken ct)
    {
        var branches = await db.BranchProducts.AsNoTracking()
            .Where(b => b.TenantId == tenantId && b.IsPublished)
            .Select(b => b.BranchId)
            .Distinct()
            .ToListAsync(ct);

        foreach (var branchId in branches)
        {
            await cache.InvalidateBranchAsync(branchId, ct);
        }

        await PublishAsync(tenantId, null, reason, ct);
    }

    private async Task PublishAsync(Guid tenantId, Guid? branchId, string reason, CancellationToken ct)
    {
        try
        {
            await bus.PublishAsync(new MenuChangedEvent(tenantId, branchId, reason), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not publish MenuChangedEvent ({Reason}) for tenant {TenantId}", reason, tenantId);
        }
    }
}
