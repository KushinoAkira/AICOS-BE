using System.Text.Json;
using StackExchange.Redis;

namespace AICOS.Ordering.Api.Services;

public record CartItemOptionDto(Guid OptionId, string OptionName, decimal ExtraPrice);

public record CartItemDto(
    Guid ItemId,
    Guid VariantId,
    Guid ProductId,
    string ProductName,
    string SizeName,
    decimal UnitPrice,
    int Quantity,
    IReadOnlyList<CartItemOptionDto> Options,
    string? Note);

public record CartDto(
    Guid SessionId,
    IReadOnlyList<CartItemDto> Items,
    int TotalItems,
    decimal EstimatedTotal);

public record AddCartItemRequest(
    Guid VariantId,
    Guid ProductId,
    string ProductName,
    string SizeName,
    decimal UnitPrice,
    int Quantity,
    List<CartItemOptionDto>? Options,
    string? Note);

public interface IRedisCartService
{
    Task<CartDto> GetCartAsync(Guid sessionId, CancellationToken ct = default);
    Task<CartDto> AddItemAsync(Guid sessionId, AddCartItemRequest request, CancellationToken ct = default);
    Task<CartDto> UpdateItemQuantityAsync(Guid sessionId, Guid itemId, int quantity, CancellationToken ct = default);
    Task<CartDto> RemoveItemAsync(Guid sessionId, Guid itemId, CancellationToken ct = default);
    Task ClearCartAsync(Guid sessionId, CancellationToken ct = default);
}

public class RedisCartService : IRedisCartService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<RedisCartService> _logger;

    // Fallback lưu trong RAM nếu Redis chưa bật ở môi trường local dev
    private static readonly Dictionary<Guid, List<CartItemDto>> FallbackStore = new();
    private static readonly object LockObj = new();

    private IConnectionMultiplexer? _redis;
    private bool _redisConnectionFailed = false;

    public RedisCartService(IConfiguration configuration, ILogger<RedisCartService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    private IDatabase? GetDatabase()
    {
        if (_redisConnectionFailed) return null;
        if (_redis != null && _redis.IsConnected) return _redis.GetDatabase();

        try
        {
            var connStr = _configuration.GetConnectionString("Redis") ?? "localhost:6379";
            var options = ConfigurationOptions.Parse(connStr);
            options.ConnectTimeout = 1000;
            options.AbortOnConnectFail = false;
            _redis = ConnectionMultiplexer.Connect(options);
            return _redis.GetDatabase();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Không thể kết nối Redis ({Message}). Đang sử dụng In-Memory Fallback Cart.", ex.Message);
            _redisConnectionFailed = true;
            return null;
        }
    }

    private string GetKey(Guid sessionId) => $"cart:session:{sessionId}";

    public async Task<CartDto> GetCartAsync(Guid sessionId, CancellationToken ct = default)
    {
        var db = GetDatabase();
        if (db != null)
        {
            try
            {
                var val = await db.StringGetAsync(GetKey(sessionId));
                if (val.HasValue)
                {
                    var items = JsonSerializer.Deserialize<List<CartItemDto>>(val!) ?? new();
                    return ToCartDto(sessionId, items);
                }
                return ToCartDto(sessionId, new List<CartItemDto>());
            }
            catch
            {
                // Fallback xuống RAM nếu Redis bị timeout
            }
        }

        lock (LockObj)
        {
            var items = FallbackStore.TryGetValue(sessionId, out var list) ? list : new List<CartItemDto>();
            return ToCartDto(sessionId, items);
        }
    }

    public async Task<CartDto> AddItemAsync(Guid sessionId, AddCartItemRequest request, CancellationToken ct = default)
    {
        var cart = await GetCartAsync(sessionId, ct);
        var items = cart.Items.ToList();

        var newItem = new CartItemDto(
            ItemId: Guid.NewGuid(),
            VariantId: request.VariantId,
            ProductId: request.ProductId,
            ProductName: request.ProductName,
            SizeName: request.SizeName,
            UnitPrice: request.UnitPrice,
            Quantity: request.Quantity <= 0 ? 1 : request.Quantity,
            Options: request.Options ?? new List<CartItemOptionDto>(),
            Note: request.Note
        );

        items.Add(newItem);
        await SaveCartItemsAsync(sessionId, items, ct);
        return ToCartDto(sessionId, items);
    }

    public async Task<CartDto> UpdateItemQuantityAsync(Guid sessionId, Guid itemId, int quantity, CancellationToken ct = default)
    {
        var cart = await GetCartAsync(sessionId, ct);
        var items = cart.Items.ToList();

        var index = items.FindIndex(i => i.ItemId == itemId);
        if (index >= 0)
        {
            if (quantity <= 0)
            {
                items.RemoveAt(index);
            }
            else
            {
                var existing = items[index];
                items[index] = existing with { Quantity = quantity };
            }
            await SaveCartItemsAsync(sessionId, items, ct);
        }

        return ToCartDto(sessionId, items);
    }

    public async Task<CartDto> RemoveItemAsync(Guid sessionId, Guid itemId, CancellationToken ct = default)
    {
        var cart = await GetCartAsync(sessionId, ct);
        var items = cart.Items.Where(i => i.ItemId != itemId).ToList();
        await SaveCartItemsAsync(sessionId, items, ct);
        return ToCartDto(sessionId, items);
    }

    public async Task ClearCartAsync(Guid sessionId, CancellationToken ct = default)
    {
        var db = GetDatabase();
        if (db != null)
        {
            try
            {
                await db.KeyDeleteAsync(GetKey(sessionId));
            }
            catch { }
        }

        lock (LockObj)
        {
            FallbackStore.Remove(sessionId);
        }
    }

    private async Task SaveCartItemsAsync(Guid sessionId, List<CartItemDto> items, CancellationToken ct)
    {
        var db = GetDatabase();
        if (db != null)
        {
            try
            {
                var json = JsonSerializer.Serialize(items);
                // Giỏ hàng lưu trữ 24 giờ
                await db.StringSetAsync(GetKey(sessionId), json, TimeSpan.FromHours(24));
                return;
            }
            catch { }
        }

        lock (LockObj)
        {
            FallbackStore[sessionId] = items;
        }
    }

    private static CartDto ToCartDto(Guid sessionId, IReadOnlyList<CartItemDto> items)
    {
        var estimatedTotal = items.Sum(i =>
            (i.UnitPrice + (i.Options?.Sum(o => o.ExtraPrice) ?? 0)) * i.Quantity
        );
        var totalItems = items.Sum(i => i.Quantity);

        return new CartDto(sessionId, items, totalItems, estimatedTotal);
    }
}
