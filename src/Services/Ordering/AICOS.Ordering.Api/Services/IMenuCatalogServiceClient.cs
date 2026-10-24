namespace AICOS.Ordering.Api.Services;

public interface IMenuCatalogServiceClient
{
    /// <summary>
    /// Kiểm tra xem các món trong đơn hàng có đang được bật bán (IsAvailable) trong Menu hay không.
    /// Quản lý có thể chủ động bật/tắt cờ IsAvailable trên MenuCatalog.
    /// </summary>
    Task<MenuAvailabilityResult> CheckItemsAvailabilityAsync(
        Guid branchId,
        IReadOnlyList<(Guid ProductId, string ProductName)> items,
        CancellationToken ct = default);
}

public record MenuAvailabilityResult(
    bool AllAvailable,
    IReadOnlyList<string>? UnavailableItems = null,
    string? Message = null);

public class MockMenuCatalogServiceClient : IMenuCatalogServiceClient
{
    private readonly ILogger<MockMenuCatalogServiceClient> _logger;

    public MockMenuCatalogServiceClient(ILogger<MockMenuCatalogServiceClient> logger)
    {
        _logger = logger;
    }

    public Task<MenuAvailabilityResult> CheckItemsAvailabilityAsync(
        Guid branchId,
        IReadOnlyList<(Guid ProductId, string ProductName)> items,
        CancellationToken ct = default)
    {
        // Mặc định mọi món trong Menu đều có IsAvailable = true.
        // Giả lập trường hợp Quản lý đã tắt cờ bán (IsAvailable = false) trên Menu (đánh dấu bằng "[TẠM HẾT]" hoặc "NGƯNG BÁN")
        var unavailable = items
            .Where(i => i.ProductName.Contains("[TẠM HẾT]", StringComparison.OrdinalIgnoreCase) ||
                        i.ProductName.Contains("[HẾT]", StringComparison.OrdinalIgnoreCase))
            .Select(i => i.ProductName)
            .ToList();

        if (unavailable.Count > 0)
        {
            _logger.LogWarning("Các món sau đang tạm ngưng phục vụ trong Menu: {Items}", string.Join(", ", unavailable));
            return Task.FromResult(new MenuAvailabilityResult(
                AllAvailable: false,
                UnavailableItems: unavailable,
                Message: $"Các món sau đang tạm ngưng phục vụ: {string.Join(", ", unavailable)}"));
        }

        return Task.FromResult(new MenuAvailabilityResult(AllAvailable: true));
    }
}
