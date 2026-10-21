using AICOS.KitchenOperations.Api.Domain.Entities;
using AICOS.KitchenOperations.Api.Features;

namespace AICOS.KitchenOperations.Api.Domain.Interfaces;

public interface IKitchenTicketRepository
{
    Task<KitchenTicket?> GetByIdAsync(Guid id);
    Task<KitchenTicket?> GetByOrderIdAsync(Guid orderId);
    
    /// <summary>
    /// Lấy hàng đợi pha chế sắp xếp theo Priority FIFO (BR-41):
    /// 1. Ưu tiên đơn có giờ hẹn gần nhất (ScheduledPickupTimeUtc)
    /// 2. Các đơn còn lại sắp xếp theo thời gian tạo đơn (CreatedAtUtc ASC)
    /// </summary>
    Task<IReadOnlyList<KitchenTicket>> GetQueueAsync(Guid branchId);

    /// <summary>
    /// Tự động gộp các món giống nhau trong cùng khung thời gian thành 1 mẻ pha chế (BR-42: 5 phút, tối đa 4 ly/mẻ)
    /// </summary>
    Task<IReadOnlyList<BrewingBatchDto>> GetActiveBatchesAsync(Guid branchId, TimeSpan? window = null, int maxBatchSize = 4);

    Task AddAsync(KitchenTicket ticket);
    Task UpdateAsync(KitchenTicket ticket);
}
