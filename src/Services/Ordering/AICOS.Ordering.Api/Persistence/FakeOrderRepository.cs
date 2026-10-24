using AICOS.Ordering.Api.Domain.Entities;
using AICOS.Ordering.Api.Domain.Interfaces;

namespace AICOS.Ordering.Api.Persistence;

public class FakeOrderRepository : IOrderRepository
{
    // Lưu tạm vào RAM trong lúc chờ database PostgreSQL từ team
    private readonly List<Order> _orders = new();
    private readonly object _lock = new();

    public Task<Order?> GetByIdAsync(Guid id)
    {
        lock (_lock)
        {
            var order = _orders.FirstOrDefault(o => o.Id == id);
            return Task.FromResult(order);
        }
    }

    public Task<Order?> GetByOrderCodeAsync(string orderCode)
    {
        lock (_lock)
        {
            var order = _orders.FirstOrDefault(o => o.OrderCode.Equals(orderCode, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(order);
        }
    }

    public Task<IEnumerable<Order>> GetAllAsync()
    {
        lock (_lock)
        {
            return Task.FromResult<IEnumerable<Order>>(_orders.OrderByDescending(o => o.CreatedAtUtc).ToList());
        }
    }

    public Task<IReadOnlyList<Order>> GetExpiredOnlineOrdersAsync(int timeoutMinutes = 5)
    {
        lock (_lock)
        {
            var expired = _orders.Where(o => o.IsOnlinePaymentExpired(timeoutMinutes)).ToList();
            return Task.FromResult<IReadOnlyList<Order>>(expired);
        }
    }

    public Task AddAsync(Order order)
    {
        lock (_lock)
        {
            _orders.Add(order);
            return Task.CompletedTask;
        }
    }

    public Task UpdateAsync(Order order)
    {
        // In-memory reference tự cập nhật, dùng lock đảm bảo thread-safe
        lock (_lock)
        {
            return Task.CompletedTask;
        }
    }
}
