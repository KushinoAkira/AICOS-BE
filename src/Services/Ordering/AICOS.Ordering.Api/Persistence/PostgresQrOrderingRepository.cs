using AICOS.Ordering.Api.Domain.Entities;
using AICOS.Ordering.Api.Domain.Enums;
using AICOS.Ordering.Api.Domain.Interfaces;
using AICOS.Ordering.Api.Persistence.Entities;
using AICOS.Ordering.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AICOS.Ordering.Api.Persistence;

public class PostgresQrOrderingRepository : IQrOrderingRepository
{
    private readonly OrderingDbContext _db;
    private readonly FakeQrOrderingRepository _fallback;
    private readonly ILogger<PostgresQrOrderingRepository> _logger;
    private bool? _canConnectDb;

    public PostgresQrOrderingRepository(
        OrderingDbContext db,
        FakeQrOrderingRepository fallback,
        ILogger<PostgresQrOrderingRepository> logger)
    {
        _db = db;
        _fallback = fallback;
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
            _logger.LogWarning("Không thể kết nối PostgreSQL. Sử dụng fallback In-Memory FakeQrOrderingRepository.");
        }

        return _canConnectDb.Value;
    }

    public async Task<(CafeTable? Table, TableArea? Area)> GetTableByQrTokenAsync(string qrToken)
    {
        if (!CanUseDatabase())
            return await _fallback.GetTableByQrTokenAsync(qrToken);

        try
        {
            var tableEntity = await _db.CafeTables
                .Include(t => t.Branch)
                .FirstOrDefaultAsync(t => t.QrCodeHash == qrToken || t.Name == qrToken);

            if (tableEntity == null)
                return await _fallback.GetTableByQrTokenAsync(qrToken);

            var domainTable = new CafeTable
            {
                Id = tableEntity.Id,
                AreaId = tableEntity.BranchId,
                TableNumber = tableEntity.Name,
                Capacity = 4,
                Status = tableEntity.Status,
                QrToken = tableEntity.QrCodeHash ?? qrToken
            };

            var domainArea = new TableArea
            {
                Id = tableEntity.BranchId,
                BranchId = tableEntity.BranchId,
                Name = tableEntity.Branch?.Name ?? "Khu vực chung",
                SortOrder = 1
            };

            return (domainTable, domainArea);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi GetTableByQrTokenAsync từ PostgreSQL, fallback về In-Memory.");
            return await _fallback.GetTableByQrTokenAsync(qrToken);
        }
    }

    public async Task<TableSession?> GetActiveSessionByTableIdAsync(Guid tableId)
    {
        if (!CanUseDatabase())
            return await _fallback.GetActiveSessionByTableIdAsync(tableId);

        try
        {
            var sessionTable = await _db.TableSessionTables
                .Include(st => st.Session)
                .Where(st => st.TableId == tableId && st.Session != null && st.Session.Status == "OPEN")
                .FirstOrDefaultAsync();

            if (sessionTable?.Session == null)
                return await _fallback.GetActiveSessionByTableIdAsync(tableId);

            return new TableSession
            {
                Id = sessionTable.Session.Id,
                BranchId = sessionTable.Session.BranchId,
                Status = sessionTable.Session.Status,
                OpenedAt = sessionTable.Session.OpenedAt,
                ClosedAt = sessionTable.Session.ClosedAt
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi GetActiveSessionByTableIdAsync từ PostgreSQL, fallback về In-Memory.");
            return await _fallback.GetActiveSessionByTableIdAsync(tableId);
        }
    }

    public async Task<TableSession> OpenSessionForTableAsync(Guid branchId, Guid tableId)
    {
        // Đồng bộ In-Memory fallback
        var fallbackSession = await _fallback.OpenSessionForTableAsync(branchId, tableId);

        if (!CanUseDatabase())
            return fallbackSession;

        try
        {
            var existingSession = await GetActiveSessionByTableIdAsync(tableId);
            if (existingSession != null)
                return existingSession;

            var newSessionEntity = new TableSessionEntity
            {
                Id = fallbackSession.Id,
                BranchId = branchId,
                Status = "OPEN",
                OpenedAt = DateTime.UtcNow
            };
            _db.TableSessions.Add(newSessionEntity);

            var sessionTableEntity = new TableSessionTableEntity
            {
                Id = Guid.NewGuid(),
                TableSessionId = newSessionEntity.Id,
                TableId = tableId
            };
            _db.TableSessionTables.Add(sessionTableEntity);

            var table = await _db.CafeTables.FirstOrDefaultAsync(t => t.Id == tableId);
            if (table != null)
            {
                table.Status = "OCCUPIED";
            }

            await _db.SaveChangesAsync();
            return fallbackSession;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi OpenSessionForTableAsync trong PostgreSQL, sử dụng kết quả fallback.");
            return fallbackSession;
        }
    }

    public async Task<TableSession?> GetSessionByIdAsync(Guid sessionId)
    {
        if (!CanUseDatabase())
            return await _fallback.GetSessionByIdAsync(sessionId);

        try
        {
            var entity = await _db.TableSessions.FirstOrDefaultAsync(s => s.Id == sessionId);
            if (entity == null)
                return await _fallback.GetSessionByIdAsync(sessionId);

            return new TableSession
            {
                Id = entity.Id,
                BranchId = entity.BranchId,
                Status = entity.Status,
                OpenedAt = entity.OpenedAt,
                ClosedAt = entity.ClosedAt
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi GetSessionByIdAsync từ PostgreSQL, fallback về In-Memory.");
            return await _fallback.GetSessionByIdAsync(sessionId);
        }
    }

    public async Task<IReadOnlyList<QrMenuCategoryDto>> GetMenuByBranchIdAsync(Guid branchId)
    {
        if (!CanUseDatabase())
            return await _fallback.GetMenuByBranchIdAsync(branchId);

        try
        {
            var branch = await _db.Branches.FirstOrDefaultAsync(b => b.Id == branchId);
            var tenantId = branch?.TenantId ?? Guid.Empty;

            var categories = await _db.Categories
                .Where(c => c.IsActive && (tenantId == Guid.Empty || c.TenantId == tenantId))
                .ToListAsync();

            if (categories.Count == 0)
                return await _fallback.GetMenuByBranchIdAsync(branchId);

            var result = new List<QrMenuCategoryDto>();
            foreach (var cat in categories)
            {
                var products = await _db.Products
                    .Include(p => p.Variants)
                    .Include(p => p.OptionGroups)
                        .ThenInclude(g => g.Options)
                    .Where(p => p.CategoryId == cat.Id && p.IsActive)
                    .ToListAsync();

                var prodDtos = new List<QrMenuProductDto>();
                foreach (var prod in products)
                {
                    var bp = await _db.BranchProducts.FirstOrDefaultAsync(b => b.BranchId == branchId && b.ProductId == prod.Id);
                    bool isAvailable = bp?.IsAvailable ?? true;

                    var variants = prod.Variants
                        .Where(v => v.IsActive)
                        .Select(v => new QrMenuVariantDto(v.Id, v.Name, v.PriceAdjustment + prod.BasePrice, v.IsActive))
                        .ToList();

                    var optGroups = prod.OptionGroups.Select(g => new QrMenuOptionGroupDto(
                        g.Id,
                        g.Name,
                        g.IsRequired ? 1 : 0,
                        g.MaxSelection,
                        g.Options.Select(o => new QrMenuOptionDto(o.Id, o.Name, o.PriceAdjustment, true)).ToList()
                    )).ToList();

                    prodDtos.Add(new QrMenuProductDto(
                        prod.Id,
                        prod.Name,
                        null,
                        null,
                        isAvailable,
                        variants,
                        optGroups
                    ));
                }

                result.Add(new QrMenuCategoryDto(cat.Id, cat.Name, 1, prodDtos));
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi GetMenuByBranchIdAsync từ PostgreSQL, fallback về In-Memory.");
            return await _fallback.GetMenuByBranchIdAsync(branchId);
        }
    }

    public async Task<ValidatedCartSummary> ValidateAndCalculateCartAsync(Guid branchId, IReadOnlyList<CartItemDto> cartItems)
    {
        if (!CanUseDatabase())
            return await _fallback.ValidateAndCalculateCartAsync(branchId, cartItems);

        try
        {
            var errors = new List<string>();
            var revalidatedItems = new List<RevalidatedCartItemDto>();
            decimal subtotal = 0;

            if (cartItems == null || cartItems.Count == 0)
            {
                return new ValidatedCartSummary(false, new[] { "Giỏ hàng rỗng." }, Array.Empty<RevalidatedCartItemDto>(), 0, 0);
            }

            foreach (var item in cartItems)
            {
                var variant = await _db.ProductVariants.Include(v => v.Product).FirstOrDefaultAsync(v => v.Id == item.VariantId && v.IsActive);
                if (variant == null)
                {
                    errors.Add($"Món '{item.ProductName}' ({item.SizeName}) không còn tồn tại hoặc đã ngừng kinh doanh.");
                    continue;
                }

                var bp = await _db.BranchProducts.FirstOrDefaultAsync(b => b.BranchId == branchId && b.ProductId == variant.ProductId);
                if (bp != null && !bp.IsAvailable)
                {
                    errors.Add($"Món '{item.ProductName}' hiện đang TẠM HẾT HÀNG tại chi nhánh này.");
                    continue;
                }

                string prodName = variant.Product?.Name ?? item.ProductName;
                var validatedOptions = new List<RevalidatedItemOptionDto>();
                decimal optionExtraTotal = 0;

                if (item.Options != null)
                {
                    foreach (var opt in item.Options)
                    {
                        var dbOpt = await _db.ProductOptions.FirstOrDefaultAsync(o => o.Id == opt.OptionId);
                        if (dbOpt != null)
                        {
                            validatedOptions.Add(new RevalidatedItemOptionDto(dbOpt.Id, dbOpt.Name, dbOpt.PriceAdjustment));
                            optionExtraTotal += dbOpt.PriceAdjustment;
                        }
                    }
                }

                decimal unitPrice = (variant.Product?.BasePrice ?? 0) + variant.PriceAdjustment;
                decimal lineTotal = (unitPrice + optionExtraTotal) * item.Quantity;
                subtotal += lineTotal;

                revalidatedItems.Add(new RevalidatedCartItemDto(
                    VariantId: variant.Id,
                    ProductId: variant.ProductId,
                    ProductName: prodName,
                    SizeName: variant.Name,
                    UnitPrice: unitPrice,
                    Quantity: item.Quantity,
                    Options: validatedOptions,
                    LineTotal: lineTotal,
                    Note: item.Note
                ));
            }

            bool isValid = errors.Count == 0;
            return new ValidatedCartSummary(isValid, errors, revalidatedItems, subtotal, subtotal);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi ValidateAndCalculateCartAsync từ PostgreSQL, fallback về In-Memory.");
            return await _fallback.ValidateAndCalculateCartAsync(branchId, cartItems);
        }
    }

    public async Task<Order> CreateOrderFromCartAsync(
        Guid branchId,
        Guid sessionId,
        Guid? customerId,
        ValidatedCartSummary validatedCart,
        string? note = null)
    {
        var order = await _fallback.CreateOrderFromCartAsync(branchId, sessionId, customerId, validatedCart, note);

        if (!CanUseDatabase())
            return order;

        try
        {
            var entity = new OrderEntity
            {
                Id = order.Id,
                BranchId = branchId,
                UserId = customerId,
                OrderCode = order.OrderCode,
                TableSessionId = sessionId,
                OrderType = "DINE_IN",
                Status = "SUBMITTED",
                TotalAmount = order.FinalAmount,
                DiscountAmount = order.DiscountAmount,
                FinalAmount = order.FinalAmount,
                PaymentStatus = "UNPAID",
                ServiceType = "DINE_IN",
                IsTakeaway = false,
                CreatedAt = DateTime.UtcNow
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

            _db.Orders.Add(entity);
            await _db.SaveChangesAsync();
            return order;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi lưu Order từ giỏ hàng vào PostgreSQL.");
            return order;
        }
    }

    public async Task<SessionBillSummaryDto?> GetSessionBillAsync(Guid sessionId)
    {
        if (!CanUseDatabase())
            return await _fallback.GetSessionBillAsync(sessionId);

        try
        {
            var session = await _db.TableSessions
                .Include(s => s.Branch)
                .Include(s => s.SessionTables)
                    .ThenInclude(st => st.Table)
                .FirstOrDefaultAsync(s => s.Id == sessionId);

            if (session == null)
                return await _fallback.GetSessionBillAsync(sessionId);

            var orders = await _db.Orders
                .Include(o => o.Items)
                .Where(o => o.TableSessionId == sessionId && o.Status != "CANCELLED")
                .ToListAsync();

            if (orders.Count == 0)
                return await _fallback.GetSessionBillAsync(sessionId);

            var orderDtos = orders.Select(o => new SessionBillOrderDto(
                OrderId: o.Id,
                OrderCode: o.OrderCode,
                Status: o.Status,
                TotalAmount: o.FinalAmount,
                CreatedAt: o.CreatedAt,
                Items: o.Items.Select(i => new RevalidatedCartItemDto(
                    VariantId: Guid.Empty,
                    ProductId: i.ProductId,
                    ProductName: $"Món {i.ProductId.ToString()[..6]}",
                    SizeName: "",
                    UnitPrice: i.UnitPrice,
                    Quantity: i.Quantity,
                    Options: Array.Empty<RevalidatedItemOptionDto>(),
                    LineTotal: i.Subtotal,
                    Note: null
                )).ToList()
            )).ToList();

            var tableName = session.SessionTables.FirstOrDefault()?.Table?.Name ?? "Tại bàn";
            return new SessionBillSummaryDto(
                SessionId: sessionId,
                TableNumber: tableName,
                AreaName: session.Branch?.Name ?? "Khu vực chính",
                Status: session.Status,
                OpenedAt: session.OpenedAt,
                TotalRounds: orders.Count,
                GrandTotal: orders.Sum(o => o.FinalAmount),
                Orders: orderDtos
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi GetSessionBillAsync từ PostgreSQL, fallback về In-Memory.");
            return await _fallback.GetSessionBillAsync(sessionId);
        }
    }

    public async Task<IReadOnlyList<SessionBillOrderDto>> GetSessionOrdersAsync(Guid sessionId)
    {
        if (!CanUseDatabase())
            return await _fallback.GetSessionOrdersAsync(sessionId);

        try
        {
            var orders = await _db.Orders
                .Include(o => o.Items)
                .Where(o => o.TableSessionId == sessionId)
                .OrderByDescending(o => o.CreatedAt)
                .ToListAsync();

            if (orders.Count == 0)
                return await _fallback.GetSessionOrdersAsync(sessionId);

            return orders.Select(o => new SessionBillOrderDto(
                OrderId: o.Id,
                OrderCode: o.OrderCode,
                Status: o.Status,
                TotalAmount: o.FinalAmount,
                CreatedAt: o.CreatedAt,
                Items: o.Items.Select(i => new RevalidatedCartItemDto(
                    VariantId: Guid.Empty,
                    ProductId: i.ProductId,
                    ProductName: $"Món {i.ProductId.ToString()[..6]}",
                    SizeName: "",
                    UnitPrice: i.UnitPrice,
                    Quantity: i.Quantity,
                    Options: Array.Empty<RevalidatedItemOptionDto>(),
                    LineTotal: i.Subtotal,
                    Note: null
                )).ToList()
            )).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi GetSessionOrdersAsync từ PostgreSQL, fallback về In-Memory.");
            return await _fallback.GetSessionOrdersAsync(sessionId);
        }
    }

    public async Task<(UserAccount User, LoyaltyAccount Loyalty)> RegisterGuestCustomerAsync(
        Guid tenantId,
        string fullName,
        string phone,
        string? email = null)
    {
        var fallbackResult = await _fallback.RegisterGuestCustomerAsync(tenantId, fullName, phone, email);

        if (!CanUseDatabase())
            return fallbackResult;

        try
        {
            var existingUser = await _db.Users.FirstOrDefaultAsync(u => u.Phone == phone);
            if (existingUser != null)
            {
                var existingLoyalty = await _db.LoyaltyAccounts.FirstOrDefaultAsync(l => l.UserId == existingUser.Id);
                return (fallbackResult.User, fallbackResult.Loyalty);
            }

            var newUser = new UserEntity
            {
                Id = fallbackResult.User.Id,
                FullName = fullName,
                Phone = phone,
                Email = email ?? $"{phone}@guest.aicos.vn",
                Status = "ACTIVE",
                CreatedAt = DateTime.UtcNow
            };
            _db.Users.Add(newUser);

            var newLoyalty = new LoyaltyAccountEntity
            {
                Id = fallbackResult.Loyalty.Id,
                TenantId = tenantId,
                UserId = newUser.Id,
                TotalPoints = 10,
                CurrentTier = "BRONZE",
                CreatedAt = DateTime.UtcNow
            };
            _db.LoyaltyAccounts.Add(newLoyalty);

            await _db.SaveChangesAsync();
            return fallbackResult;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi RegisterGuestCustomerAsync trong PostgreSQL.");
            return fallbackResult;
        }
    }
}
