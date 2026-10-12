using Microsoft.EntityFrameworkCore;

namespace AICOS.BuildingBlocks.Persistence;

/// <summary>Standard list response of every service: { items, page, pageSize, totalCount }.</summary>
public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public static class PagingExtensions
{
    public const int MaxPageSize = 100;

    /// <summary>Clamps paging input (page >= 1, 1 <= pageSize <= 100), counts, takes one page and maps it.</summary>
    public static async Task<PagedResponse<TResult>> ToPagedAsync<TEntity, TResult>(
        this IQueryable<TEntity> query, int page, int pageSize, Func<TEntity, TResult> map, CancellationToken ct)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var total = await query.CountAsync(ct);
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResponse<TResult>(items.Select(map).ToList(), page, pageSize, total);
    }
}
