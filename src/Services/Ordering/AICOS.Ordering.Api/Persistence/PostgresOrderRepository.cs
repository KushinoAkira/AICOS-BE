using AICOS.BuildingBlocks.Security;
using AICOS.Ordering.Api.Domain.Entities;
using AICOS.Ordering.Api.Domain.Enums;
using AICOS.Ordering.Api.Domain.Interfaces;
using AICOS.Ordering.Api.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AICOS.Ordering.Api.Persistence;

public class PostgresOrderRepository : IOrderRepository
{
    private readonly OrderingDbContext _db;
    private readonly FakeOrderRepository _fallback;
    private readonly ICurrentUser _currentUser;
    private readonly ILogger<PostgresOrderRepository> _logger;
    private bool? _canConnectDb;

    public PostgresOrderRepository(
        OrderingDbContext db,
        FakeOrderRepository fallback,
        ICurrentUser currentUser,
        ILogger<PostgresOrderRepository> logger)
    {
        _db = db;
        _fallback = fallback;
        _currentUser = currentUser;
        _logger = logger;
    }

    private bool CanUseDatabase()
    {
        if (_canConnectDb.HasValue)
            return _canConnectDb.Value;

        try
        {
            _canConnectDb = _db.Database.CanConnect();
        }
        catch
        {
            _canConnectDb = false;
        }

        if (!_canConnectDb.Value)
        {
            _logger.LogWarning("Không thể kết nối PostgreSQL. Sử dụng fallback In-Memory FakeOrderRepository.");
        }

        return _canConnectDb.Value;
    }

    public async Task<Order?> GetByIdAsync(Guid id)
    {
        if (!CanUseDatabase())
            return await _fallback.GetByIdAsync(id);

        try
        {
            var entity = await _db.Orders
                .Include(o => o.Items)
                .Include(o => o.Bill)
                .Include(o => o.Branch)
                .FirstOrDefaultAsync(o => o.Id == id);

            if (entity == null)
                return await _fallback.GetByIdAsync(id);

            // Multitenancy check: nếu caller có tenant_id thì phải khớp
            if (_currentUser.TenantId.HasValue && entity.Branch != null && entity.Branch.TenantId != _currentUser.TenantId.Value)
            {
                return null;
            }

            return MapToDomain(entity);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi khi truy vấn GetByIdAsync từ PostgreSQL, fallback về In-Memory.");
            return await _fallback.GetByIdAsync(id);
        }
    }

    public async Task<Order?> GetByOrderCodeAsync(string orderCode)
    {
        if (!CanUseDatabase())
            return await _fallback.GetByOrderCodeAsync(orderCode);

        try
        {
            var entity = await _db.Orders
                .Include(o => o.Items)
                .Include(o => o.Bill)
                .Include(o => o.Branch)
                .FirstOrDefaultAsync(o => o.OrderCode.ToLower() == orderCode.ToLower());

            if (entity == null)
                return await _fallback.GetByOrderCodeAsync(orderCode);

            if (_currentUser.TenantId.HasValue && entity.Branch != null && entity.Branch.TenantId != _currentUser.TenantId.Value)
            {
                return null;
            }

            return MapToDomain(entity);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi khi truy vấn GetByOrderCodeAsync từ PostgreSQL, fallback về In-Memory.");
            return await _fallback.GetByOrderCodeAsync(orderCode);
        }
    }

    public async Task<IEnumerable<Order>> GetAllAsync()
    {
        if (!CanUseDatabase())
            return await _fallback.GetAllAsync();

        try
        {
            IQueryable<OrderEntity> query = _db.Orders
                .Include(o => o.Items)
                .Include(o => o.Bill)
                .Include(o => o.Branch);

            // 1. Phân quyền theo chi nhánh (BranchId) nếu caller gắn với chi nhánh
            if (_currentUser.BranchId.HasValue)
            {
                query = query.Where(o => o.BranchId == _currentUser.BranchId.Value);
            }
            // 2. Phân quyền theo Tenant (TenantId) nếu caller là tenant admin / staff
            else if (_currentUser.TenantId.HasValue)
            {
                query = query.Where(o => o.Branch != null && o.Branch.TenantId == _currentUser.TenantId.Value);
            }

            var entities = await query.OrderByDescending(o => o.CreatedAt).ToListAsync();
            return entities.Select(MapToDomain).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi khi truy vấn GetAllAsync từ PostgreSQL, fallback về In-Memory.");
            return await _fallback.GetAllAsync();
        }
    }

    public async Task<IReadOnlyList<Order>> GetExpiredOnlineOrdersAsync(int timeoutMinutes = 5)
    {
        if (!CanUseDatabase())
            return await _fallback.GetExpiredOnlineOrdersAsync(timeoutMinutes);

        try
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-timeoutMinutes);
            var entities = await _db.Orders
                .Include(o => o.Items)
                .Include(o => o.Bill)
                .Where(o => o.PaymentStatus == "UNPAID"
                            && o.Status != "CANCELLED"
                            && o.Status != "COMPLETED"
                            && (o.OrderType == "DELIVERY" || o.OrderType == "ONLINE" || o.OrderType == "TAKEAWAY")
                            && o.CreatedAt <= cutoff)
                .ToListAsync();

            return entities.Select(MapToDomain).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi khi lấy đơn online hết hạn từ PostgreSQL, fallback về In-Memory.");
            return await _fallback.GetExpiredOnlineOrdersAsync(timeoutMinutes);
        }
    }

    public async Task AddAsync(Order order)
    {
        // Đồng bộ In-Memory fallback
        await _fallback.AddAsync(order);

        if (!CanUseDatabase())
            return;

        try
        {
            var entity = MapToEntity(order);
            _db.Orders.Add(entity);
            await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi khi lưu Order {OrderCode} vào PostgreSQL.", order.OrderCode);
        }
    }

    public async Task UpdateAsync(Order order)
    {
        // Đồng bộ In-Memory fallback
        await _fallback.UpdateAsync(order);

        if (!CanUseDatabase())
            return;

        try
        {
            var entity = await _db.Orders
                .Include(o => o.Items)
                .Include(o => o.Bill)
                .FirstOrDefaultAsync(o => o.Id == order.Id);

            if (entity != null)
            {
                entity.Status = order.Status.ToString().ToUpper();
                entity.PaymentStatus = order.PaymentStatus.ToString().ToUpper();
                entity.TotalAmount = order.OriginalAmount;
                entity.DiscountAmount = order.DiscountAmount;
                entity.FinalAmount = order.FinalAmount;
                entity.OrderType = order.Type.ToString().ToUpper();
                entity.UpdatedAt = DateTime.UtcNow;

                if (order.PaymentStatus == PaymentStatus.Paid && order.PaymentMethod.HasValue && entity.Bill == null)
                {
                    entity.Bill = new BillEntity
                    {
                        Id = Guid.NewGuid(),
                        OrderId = order.Id,
                        BillCode = $"BILL-{order.OrderCode}",
                        TotalAmount = order.OriginalAmount,
                        DiscountAmount = order.DiscountAmount,
                        FinalAmount = order.FinalAmount,
                        PaymentMethod = order.PaymentMethod.Value.ToString().ToUpper(),
                        PaymentTime = DateTime.UtcNow,
                        Status = "PAID"
                    };
                }

                await _db.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi khi cập nhật Order {OrderCode} vào PostgreSQL.", order.OrderCode);
        }
    }

    // --- Helpers Mappers ---

    private static Order MapToDomain(OrderEntity entity)
    {
        var type = Enum.TryParse<OrderType>(entity.OrderType, true, out var t) ? t : OrderType.DineIn;
        var paymentMethod = Enum.TryParse<PaymentMethod>(entity.Bill?.PaymentMethod, true, out var pm) ? pm : (PaymentMethod?)null;

        var order = new Order(
            branchId: entity.BranchId,
            type: type,
            cashierId: entity.UserId,
            customerId: null,
            tableId: null,
            tableNumber: entity.TableSessionId.HasValue ? "Tại bàn" : null,
            deliveryAddress: null,
            paymentMethod: paymentMethod,
            note: null
        );

        SetProperty(order, nameof(Order.Id), entity.Id);
        SetProperty(order, nameof(Order.OrderCode), entity.OrderCode);

        if (Enum.TryParse<OrderStatus>(entity.Status, true, out var st))
        {
            SetProperty(order, nameof(Order.Status), st);
        }
        if (Enum.TryParse<PaymentStatus>(entity.PaymentStatus, true, out var ps))
        {
            SetProperty(order, nameof(Order.PaymentStatus), ps);
        }

        SetProperty(order, nameof(Order.DiscountAmount), entity.DiscountAmount);
        SetProperty(order, nameof(Order.CreatedAtUtc), entity.CreatedAt);

        foreach (var item in entity.Items)
        {
            order.AddItem(item.ProductId, $"Món {item.ProductId.ToString()[..6]}", item.UnitPrice, item.Quantity, null);
        }

        return order;
    }

    private static OrderEntity MapToEntity(Order order)
    {
        var entity = new OrderEntity
        {
            Id = order.Id,
            BranchId = order.BranchId,
            UserId = order.CashierId,
            OrderCode = order.OrderCode,
            TableSessionId = order.TableId,
            OrderType = order.Type.ToString().ToUpper(),
            Status = order.Status.ToString().ToUpper(),
            TotalAmount = order.OriginalAmount,
            DiscountAmount = order.DiscountAmount,
            FinalAmount = order.FinalAmount,
            PaymentStatus = order.PaymentStatus.ToString().ToUpper(),
            ServiceType = order.Type.ToString().ToUpper(),
            CustomerPhone = null,
            IsTakeaway = order.Type == OrderType.Takeaway,
            CreatedAt = order.CreatedAtUtc,
            UpdatedAt = order.CompletedAtUtc ?? order.CancelledAtUtc
        };

        foreach (var item in order.Items)
        {
            entity.Items.Add(new OrderItemEntity
            {
                Id = item.Id,
                OrderId = order.Id,
                ProductId = item.ProductId,
                Quantity = item.Quantity,
                UnitPrice = item.UnitPrice,
                Subtotal = item.TotalPrice,
                PreparationStatus = "PENDING"
            });
        }

        if (order.PaymentStatus == PaymentStatus.Paid && order.PaymentMethod.HasValue)
        {
            entity.Bill = new BillEntity
            {
                Id = Guid.NewGuid(),
                OrderId = order.Id,
                BillCode = $"BILL-{order.OrderCode}",
                TotalAmount = order.OriginalAmount,
                DiscountAmount = order.DiscountAmount,
                FinalAmount = order.FinalAmount,
                PaymentMethod = order.PaymentMethod.Value.ToString().ToUpper(),
                PaymentTime = DateTime.UtcNow,
                Status = "PAID"
            };
        }

        return entity;
    }

    private static void SetProperty<T>(object target, string propertyName, T value)
    {
        var prop = target.GetType().GetProperty(propertyName);
        prop?.SetValue(target, value);
    }
}
