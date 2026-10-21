using AICOS.KitchenOperations.Api.Domain.Entities;
using AICOS.KitchenOperations.Api.Domain.Enums;
using AICOS.KitchenOperations.Api.Domain.Interfaces;
using AICOS.KitchenOperations.Api.Features;

namespace AICOS.KitchenOperations.Api.Persistence;

public class FakeKitchenTicketRepository : IKitchenTicketRepository
{
    private readonly List<KitchenTicket> _tickets = new();
    private readonly object _lock = new();

    public Task<KitchenTicket?> GetByIdAsync(Guid id)
    {
        lock (_lock)
        {
            var ticket = _tickets.FirstOrDefault(t => t.Id == id);
            return Task.FromResult(ticket);
        }
    }

    public Task<KitchenTicket?> GetByOrderIdAsync(Guid orderId)
    {
        lock (_lock)
        {
            var ticket = _tickets.FirstOrDefault(t => t.OrderId == orderId);
            return Task.FromResult(ticket);
        }
    }

    public Task<IReadOnlyList<KitchenTicket>> GetQueueAsync(Guid branchId)
    {
        lock (_lock)
        {
            var active = _tickets
                .Where(t => t.BranchId == branchId &&
                            t.Status != KitchenTicketStatus.Completed &&
                            t.Status != KitchenTicketStatus.Cancelled)
                .ToList();

            // BR-41: Xử lý theo FIFO trừ khi được đánh dấu ưu tiên (giờ hẹn gần nhất)
            var priorityTickets = active
                .Where(t => t.IsPriority)
                .OrderBy(t => t.ScheduledPickupTimeUtc)
                .ToList();

            var regularTickets = active
                .Where(t => !t.IsPriority)
                .OrderBy(t => t.CreatedAtUtc)
                .ToList();

            var sortedQueue = priorityTickets.Concat(regularTickets).ToList();
            return Task.FromResult<IReadOnlyList<KitchenTicket>>(sortedQueue);
        }
    }

    public Task<IReadOnlyList<BrewingBatchDto>> GetActiveBatchesAsync(
        Guid branchId,
        TimeSpan? window = null,
        int maxBatchSize = 4)
    {
        lock (_lock)
        {
            var batchWindow = window ?? TimeSpan.FromMinutes(5);
            var thresholdTime = DateTime.UtcNow - batchWindow;

            // Lấy các đơn đang chờ hoặc đang pha trong khung thời gian 5 phút
            var activeTickets = _tickets
                .Where(t => t.BranchId == branchId &&
                            (t.Status == KitchenTicketStatus.Pending || t.Status == KitchenTicketStatus.Brewing) &&
                            t.CreatedAtUtc >= thresholdTime)
                .OrderBy(t => t.CreatedAtUtc)
                .ToList();

            // Mở rộng thành từng ly đơn lẻ kèm thông tin đơn
            var allCups = new List<(string ProductName, BatchCupItemDto Cup)>();

            foreach (var ticket in activeTickets)
            {
                foreach (var item in ticket.Items)
                {
                    for (int i = 0; i < item.Quantity; i++)
                    {
                        allCups.Add((
                            item.ProductName,
                            new BatchCupItemDto(
                                TicketId: ticket.Id,
                                OrderCode: ticket.OrderCode,
                                TableNumber: ticket.TableNumber,
                                Note: item.Note,
                                CreatedAtUtc: ticket.CreatedAtUtc
                            )
                        ));
                    }
                }
            }

            // BR-42: Gộp danh nghĩa theo tên món, chia mẻ tối đa 4 ly
            var batches = new List<BrewingBatchDto>();

            var groupedByProduct = allCups.GroupBy(c => c.ProductName);

            foreach (var group in groupedByProduct)
            {
                var cupsList = group.Select(x => x.Cup).ToList();
                
                // Chia thành các mẻ tối đa maxBatchSize (4 ly)
                for (int i = 0; i < cupsList.Count; i += maxBatchSize)
                {
                    var chunk = cupsList.Skip(i).Take(maxBatchSize).ToList();
                    batches.Add(new BrewingBatchDto(
                        ProductName: group.Key,
                        TotalQuantity: chunk.Count,
                        MaxBatchCapacity: maxBatchSize,
                        Cups: chunk
                    ));
                }
            }

            return Task.FromResult<IReadOnlyList<BrewingBatchDto>>(batches);
        }
    }

    public Task AddAsync(KitchenTicket ticket)
    {
        lock (_lock)
        {
            _tickets.Add(ticket);
            return Task.CompletedTask;
        }
    }

    public Task UpdateAsync(KitchenTicket ticket)
    {
        lock (_lock)
        {
            return Task.CompletedTask;
        }
    }
}
