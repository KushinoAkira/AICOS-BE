using AICOS.Ordering.Api.Domain.Entities;

namespace AICOS.Ordering.Api.Domain.Interfaces;

public interface IOrderRepository
{
    Task<Order?> GetByIdAsync(Guid id);
    Task<Order?> GetByOrderCodeAsync(string orderCode);
    Task<IEnumerable<Order>> GetAllAsync();
    Task<IReadOnlyList<Order>> GetExpiredOnlineOrdersAsync(int timeoutMinutes = 5);
    Task AddAsync(Order order);
    Task UpdateAsync(Order order);
}
