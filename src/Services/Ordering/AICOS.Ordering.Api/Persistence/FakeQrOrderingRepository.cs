using AICOS.Ordering.Api.Domain.Entities;
using AICOS.Ordering.Api.Domain.Enums;
using AICOS.Ordering.Api.Domain.Interfaces;
using AICOS.Ordering.Api.Services;

namespace AICOS.Ordering.Api.Persistence;

public class FakeQrOrderingRepository : IQrOrderingRepository
{
    private readonly object _lock = new();

    // Data collections simulating SQL Server tables
    private readonly List<TableArea> _areas = new();
    private readonly List<CafeTable> _tables = new();
    private readonly List<TableSession> _sessions = new();
    private readonly List<TableSessionTable> _sessionTables = new();
    private readonly List<Category> _categories = new();
    private readonly List<Product> _products = new();
    private readonly List<ProductVariant> _variants = new();
    private readonly List<ProductOptionGroup> _optionGroups = new();
    private readonly List<ProductOption> _options = new();
    private readonly List<BranchProduct> _branchProducts = new();
    private readonly List<Order> _orders = new();
    private readonly List<OrderItemOption> _orderItemOptions = new();
    private readonly List<OrderItemEvent> _orderItemEvents = new();
    private readonly List<UserAccount> _users = new();
    private readonly List<LoyaltyAccount> _loyaltyAccounts = new();

    public static readonly Guid DefaultTenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid DefaultBranchId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public FakeQrOrderingRepository()
    {
        SeedMockData();
    }

    private void SeedMockData()
    {
        // 1. Khởi tạo Khu vực & Bàn cafe
        var area1 = new TableArea { Id = Guid.NewGuid(), BranchId = DefaultBranchId, Name = "Tầng 1 - Phòng Máy Lạnh", SortOrder = 1 };
        var area2 = new TableArea { Id = Guid.NewGuid(), BranchId = DefaultBranchId, Name = "Sân Vườn Ngoài Trời", SortOrder = 2 };
        _areas.AddRange(new[] { area1, area2 });

        var table1 = new CafeTable { Id = Guid.NewGuid(), AreaId = area1.Id, TableNumber = "Bàn 01", Capacity = 4, Status = "AVAILABLE", QrToken = "QR-TABLE-01" };
        var table2 = new CafeTable { Id = Guid.NewGuid(), AreaId = area1.Id, TableNumber = "Bàn 02", Capacity = 2, Status = "AVAILABLE", QrToken = "QR-TABLE-02" };
        var table3 = new CafeTable { Id = Guid.NewGuid(), AreaId = area2.Id, TableNumber = "Bàn 03", Capacity = 6, Status = "AVAILABLE", QrToken = "QR-TABLE-03" };
        _tables.AddRange(new[] { table1, table2, table3 });

        // 2. Danh mục sản phẩm
        var catCoffee = new Category { Id = Guid.NewGuid(), TenantId = DefaultTenantId, Name = "Cà Phê Truyền Thống", SortOrder = 1 };
        var catTea = new Category { Id = Guid.NewGuid(), TenantId = DefaultTenantId, Name = "Trà Trái Cây & Trà Sữa", SortOrder = 2 };
        _categories.AddRange(new[] { catCoffee, catTea });

        // 3. Sản phẩm 1: Cà phê sữa đá
        var prod1 = new Product { Id = Guid.NewGuid(), TenantId = DefaultTenantId, CategoryId = catCoffee.Id, Name = "Cà Phê Sữa Đá", Description = "Cà phê Robusta đậm vị pha phin truyền thống" };
        var v1M = new ProductVariant { Id = Guid.NewGuid(), ProductId = prod1.Id, SizeName = "Size M", Price = 32000 };
        var v1L = new ProductVariant { Id = Guid.NewGuid(), ProductId = prod1.Id, SizeName = "Size L", Price = 38000 };
        var g1Sugar = new ProductOptionGroup { Id = Guid.NewGuid(), ProductId = prod1.Id, Name = "Mức ngọt", MinSelect = 1, MaxSelect = 1 };
        var o1Sugar1 = new ProductOption { Id = Guid.NewGuid(), GroupId = g1Sugar.Id, Name = "100% đường sữa (bình thường)", ExtraPrice = 0 };
        var o1Sugar2 = new ProductOption { Id = Guid.NewGuid(), GroupId = g1Sugar.Id, Name = "50% đường sữa (ít ngọt)", ExtraPrice = 0 };

        // 4. Sản phẩm 2: Trà Sữa Ô Long
        var prod2 = new Product { Id = Guid.NewGuid(), TenantId = DefaultTenantId, CategoryId = catTea.Id, Name = "Trà Sữa Ô Long", Description = "Trà Ô Long nướng thơm lừng kết hợp sữa béo ngậy" };
        var v2M = new ProductVariant { Id = Guid.NewGuid(), ProductId = prod2.Id, SizeName = "Size M", Price = 38000 };
        var v2L = new ProductVariant { Id = Guid.NewGuid(), ProductId = prod2.Id, SizeName = "Size L", Price = 45000 };
        var g2Topping = new ProductOptionGroup { Id = Guid.NewGuid(), ProductId = prod2.Id, Name = "Topping thêm", MinSelect = 0, MaxSelect = 2 };
        var o2Top1 = new ProductOption { Id = Guid.NewGuid(), GroupId = g2Topping.Id, Name = "Trân châu đen", ExtraPrice = 8000 };
        var o2Top2 = new ProductOption { Id = Guid.NewGuid(), GroupId = g2Topping.Id, Name = "Pudding trứng", ExtraPrice = 10000 };

        // 5. Sản phẩm 3: Cà phê muối (TẠM HẾT HÀNG để test revalidation)
        var prod3 = new Product { Id = Guid.NewGuid(), TenantId = DefaultTenantId, CategoryId = catCoffee.Id, Name = "Cà Phê Muối Kem Béo", Description = "Cà phê kem muối mặn mà" };
        var v3M = new ProductVariant { Id = Guid.NewGuid(), ProductId = prod3.Id, SizeName = "Size M", Price = 39000 };

        _products.AddRange(new[] { prod1, prod2, prod3 });
        _variants.AddRange(new[] { v1M, v1L, v2M, v2L, v3M });
        _optionGroups.AddRange(new[] { g1Sugar, g2Topping });
        _options.AddRange(new[] { o1Sugar1, o1Sugar2, o2Top1, o2Top2 });

        // Cấu hình còn hàng / hết hàng theo chi nhánh (branch_products)
        _branchProducts.Add(new BranchProduct { BranchId = DefaultBranchId, ProductId = prod1.Id, IsAvailable = true });
        _branchProducts.Add(new BranchProduct { BranchId = DefaultBranchId, ProductId = prod2.Id, IsAvailable = true });
        _branchProducts.Add(new BranchProduct { BranchId = DefaultBranchId, ProductId = prod3.Id, IsAvailable = false }); // Hết hàng!
    }

    public Task<(CafeTable? Table, TableArea? Area)> GetTableByQrTokenAsync(string qrToken)
    {
        lock (_lock)
        {
            var table = _tables.FirstOrDefault(t => t.QrToken.Equals(qrToken, StringComparison.OrdinalIgnoreCase));
            if (table == null) return Task.FromResult<(CafeTable?, TableArea?)>((null, null));

            var area = _areas.FirstOrDefault(a => a.Id == table.AreaId);
            return Task.FromResult<(CafeTable?, TableArea?)>((table, area));
        }
    }

    public Task<TableSession?> GetActiveSessionByTableIdAsync(Guid tableId)
    {
        lock (_lock)
        {
            var activeSessionTable = _sessionTables
                .Where(st => st.TableId == tableId && st.LeftAt == null)
                .Join(_sessions.Where(s => s.Status == "OPEN"),
                      st => st.SessionId,
                      s => s.Id,
                      (st, s) => s)
                .FirstOrDefault();

            return Task.FromResult(activeSessionTable);
        }
    }

    public Task<TableSession> OpenSessionForTableAsync(Guid branchId, Guid tableId)
    {
        lock (_lock)
        {
            // Kiểm tra xem có session OPEN nào chưa
            var existing = _sessionTables
                .Where(st => st.TableId == tableId && st.LeftAt == null)
                .Join(_sessions.Where(s => s.Status == "OPEN"),
                      st => st.SessionId,
                      s => s.Id,
                      (st, s) => s)
                .FirstOrDefault();

            if (existing != null)
            {
                return Task.FromResult(existing);
            }

            // Tạo session mới
            var newSession = new TableSession
            {
                Id = Guid.NewGuid(),
                BranchId = branchId,
                Status = "OPEN",
                OpenedAt = DateTime.UtcNow
            };
            _sessions.Add(newSession);

            var sessionTable = new TableSessionTable
            {
                Id = Guid.NewGuid(),
                SessionId = newSession.Id,
                TableId = tableId,
                JoinedAt = DateTime.UtcNow
            };
            _sessionTables.Add(sessionTable);

            // Cập nhật trạng thái bàn sang OCCUPIED
            var table = _tables.FirstOrDefault(t => t.Id == tableId);
            if (table != null) table.Status = "OCCUPIED";

            return Task.FromResult(newSession);
        }
    }

    public Task<TableSession?> GetSessionByIdAsync(Guid sessionId)
    {
        lock (_lock)
        {
            var session = _sessions.FirstOrDefault(s => s.Id == sessionId);
            return Task.FromResult(session);
        }
    }

    public Task<IReadOnlyList<QrMenuCategoryDto>> GetMenuByBranchIdAsync(Guid branchId)
    {
        lock (_lock)
        {
            var result = new List<QrMenuCategoryDto>();

            var categories = _categories.Where(c => c.IsActive).OrderBy(c => c.SortOrder).ToList();

            foreach (var cat in categories)
            {
                var prodsInCat = _products.Where(p => p.CategoryId == cat.Id && p.IsActive).ToList();
                var prodDtos = new List<QrMenuProductDto>();

                foreach (var prod in prodsInCat)
                {
                    var bp = _branchProducts.FirstOrDefault(b => b.BranchId == branchId && b.ProductId == prod.Id);
                    bool isAvailable = bp?.IsAvailable ?? true;

                    var variants = _variants
                        .Where(v => v.ProductId == prod.Id && v.IsActive)
                        .Select(v => new QrMenuVariantDto(v.Id, v.SizeName, v.Price, v.IsActive))
                        .ToList();

                    var optGroups = _optionGroups
                        .Where(g => g.ProductId == prod.Id)
                        .Select(g => new QrMenuOptionGroupDto(
                            g.Id,
                            g.Name,
                            g.MinSelect,
                            g.MaxSelect,
                            _options.Where(o => o.GroupId == g.Id && o.IsActive)
                                    .Select(o => new QrMenuOptionDto(o.Id, o.Name, o.ExtraPrice, o.IsActive))
                                    .ToList()
                        )).ToList();

                    prodDtos.Add(new QrMenuProductDto(
                        prod.Id,
                        prod.Name,
                        prod.Description,
                        prod.ImageUrl,
                        isAvailable,
                        variants,
                        optGroups
                    ));
                }

                result.Add(new QrMenuCategoryDto(cat.Id, cat.Name, cat.SortOrder, prodDtos));
            }

            return Task.FromResult<IReadOnlyList<QrMenuCategoryDto>>(result);
        }
    }

    public Task<ValidatedCartSummary> ValidateAndCalculateCartAsync(Guid branchId, IReadOnlyList<CartItemDto> cartItems)
    {
        lock (_lock)
        {
            var errors = new List<string>();
            var revalidatedItems = new List<RevalidatedCartItemDto>();
            decimal subtotal = 0;

            if (cartItems == null || cartItems.Count == 0)
            {
                return Task.FromResult(new ValidatedCartSummary(false, new[] { "Giỏ hàng rỗng." }, Array.Empty<RevalidatedCartItemDto>(), 0, 0));
            }

            foreach (var item in cartItems)
            {
                // 1. Kiểm tra variant
                var variant = _variants.FirstOrDefault(v => v.Id == item.VariantId && v.IsActive);
                if (variant == null)
                {
                    errors.Add($"Món '{item.ProductName}' ({item.SizeName}) không còn tồn tại hoặc đã ngừng kinh doanh.");
                    continue;
                }

                // 2. Kiểm tra cờ IsAvailable tại chi nhánh (branch_products)
                var bp = _branchProducts.FirstOrDefault(b => b.BranchId == branchId && b.ProductId == variant.ProductId);
                if (bp != null && !bp.IsAvailable)
                {
                    errors.Add($"Món '{item.ProductName}' hiện đang TẠM HẾT HÀNG tại chi nhánh này.");
                    continue;
                }

                var product = _products.FirstOrDefault(p => p.Id == variant.ProductId);
                string prodName = product?.Name ?? item.ProductName;

                // 3. Kiểm tra các option và giá tiền
                var validatedOptions = new List<RevalidatedItemOptionDto>();
                decimal optionExtraTotal = 0;

                if (item.Options != null)
                {
                    foreach (var opt in item.Options)
                    {
                        var dbOpt = _options.FirstOrDefault(o => o.Id == opt.OptionId && o.IsActive);
                        if (dbOpt != null)
                        {
                            validatedOptions.Add(new RevalidatedItemOptionDto(dbOpt.Id, dbOpt.Name, dbOpt.ExtraPrice));
                            optionExtraTotal += dbOpt.ExtraPrice;
                        }
                    }
                }

                // 4. Tính toán chính xác phía Server
                decimal unitPrice = variant.Price;
                decimal lineTotal = (unitPrice + optionExtraTotal) * item.Quantity;
                subtotal += lineTotal;

                revalidatedItems.Add(new RevalidatedCartItemDto(
                    VariantId: variant.Id,
                    ProductId: variant.ProductId,
                    ProductName: prodName,
                    SizeName: variant.SizeName,
                    UnitPrice: unitPrice,
                    Quantity: item.Quantity,
                    Options: validatedOptions,
                    LineTotal: lineTotal,
                    Note: item.Note
                ));
            }

            bool isValid = errors.Count == 0;
            return Task.FromResult(new ValidatedCartSummary(isValid, errors, revalidatedItems, subtotal, subtotal));
        }
    }

    public Task<Order> CreateOrderFromCartAsync(
        Guid branchId,
        Guid sessionId,
        Guid? customerId,
        ValidatedCartSummary validatedCart,
        string? note = null)
    {
        lock (_lock)
        {
            // Lấy thông tin bàn từ session
            var sessionTable = _sessionTables.FirstOrDefault(st => st.SessionId == sessionId && st.LeftAt == null);
            var table = sessionTable != null ? _tables.FirstOrDefault(t => t.Id == sessionTable.TableId) : null;

            // The Guest QR flow is not tenant-aware yet (no tenant on the QR session), so its orders carry an empty tenant and never reach the real queues.
            var order = new Order(
                tenantId: Guid.Empty,
                now: DateTime.UtcNow,
                branchId: branchId,
                type: OrderType.DineIn,
                customerId: customerId,
                tableId: table?.Id,
                tableNumber: table?.TableNumber ?? "Tại bàn",
                note: note
            );

            foreach (var item in validatedCart.Items)
            {
                // Định dạng tên món kèm Size và tùy chọn
                string optionNames = item.Options.Count > 0 ? $" ({string.Join(", ", item.Options.Select(o => o.OptionName))})" : "";
                string fullDisplayName = $"{item.ProductName} [{item.SizeName}]{optionNames}";
                decimal effectiveUnitPrice = item.UnitPrice + item.Options.Sum(o => o.ExtraPrice);

                order.AddItem(item.ProductId, fullDisplayName, effectiveUnitPrice, item.Quantity, item.Note);

                // Ghi nhận log event order_item_events
                _orderItemEvents.Add(new OrderItemEvent
                {
                    Id = Guid.NewGuid(),
                    OrderItemId = Guid.NewGuid(),
                    EventType = "ORDER_CREATED",
                    NewStatus = "PENDING",
                    Note = $"Khách quét QR gọi món tại {table?.TableNumber}"
                });
            }

            _orders.Add(order);
            return Task.FromResult(order);
        }
    }

    public Task<SessionBillSummaryDto?> GetSessionBillAsync(Guid sessionId)
    {
        lock (_lock)
        {
            var session = _sessions.FirstOrDefault(s => s.Id == sessionId);
            if (session == null) return Task.FromResult<SessionBillSummaryDto?>(null);

            var sessionTable = _sessionTables.FirstOrDefault(st => st.SessionId == sessionId && st.LeftAt == null);
            var table = sessionTable != null ? _tables.FirstOrDefault(t => t.Id == sessionTable.TableId) : null;
            var area = table != null ? _areas.FirstOrDefault(a => a.Id == table.AreaId) : null;

            // Tìm các order của bàn này
            var orders = _orders.Where(o => o.TableId == table?.Id && o.Status != OrderStatus.Cancelled).ToList();

            var orderDtos = orders.Select(o => new SessionBillOrderDto(
                OrderId: o.Id,
                OrderCode: o.OrderCode,
                Status: o.Status.ToString(),
                TotalAmount: o.TotalAmount,
                CreatedAt: o.CreatedAtUtc,
                Items: o.Items.Select(i => new RevalidatedCartItemDto(
                    VariantId: Guid.Empty,
                    ProductId: i.ProductId,
                    ProductName: i.ProductName,
                    SizeName: "",
                    UnitPrice: i.UnitPrice,
                    Quantity: i.Quantity,
                    Options: Array.Empty<RevalidatedItemOptionDto>(),
                    LineTotal: i.TotalPrice,
                    Note: i.Note
                )).ToList()
            )).ToList();

            decimal grandTotal = orders.Sum(o => o.TotalAmount);

            var summary = new SessionBillSummaryDto(
                SessionId: sessionId,
                TableNumber: table?.TableNumber ?? "Không xác định",
                AreaName: area?.Name ?? "Chưa phân khu",
                Status: session.Status,
                OpenedAt: session.OpenedAt,
                TotalRounds: orders.Count,
                GrandTotal: grandTotal,
                Orders: orderDtos
            );

            return Task.FromResult<SessionBillSummaryDto?>(summary);
        }
    }

    public Task<IReadOnlyList<SessionBillOrderDto>> GetSessionOrdersAsync(Guid sessionId)
    {
        lock (_lock)
        {
            var sessionTable = _sessionTables.FirstOrDefault(st => st.SessionId == sessionId && st.LeftAt == null);
            var table = sessionTable != null ? _tables.FirstOrDefault(t => t.Id == sessionTable.TableId) : null;

            var orders = _orders.Where(o => o.TableId == table?.Id).OrderByDescending(o => o.CreatedAtUtc).ToList();

            var dtos = orders.Select(o => new SessionBillOrderDto(
                OrderId: o.Id,
                OrderCode: o.OrderCode,
                Status: o.Status.ToString(),
                TotalAmount: o.TotalAmount,
                CreatedAt: o.CreatedAtUtc,
                Items: o.Items.Select(i => new RevalidatedCartItemDto(
                    VariantId: Guid.Empty,
                    ProductId: i.ProductId,
                    ProductName: i.ProductName,
                    SizeName: "",
                    UnitPrice: i.UnitPrice,
                    Quantity: i.Quantity,
                    Options: Array.Empty<RevalidatedItemOptionDto>(),
                    LineTotal: i.TotalPrice,
                    Note: i.Note
                )).ToList()
            )).ToList();

            return Task.FromResult<IReadOnlyList<SessionBillOrderDto>>(dtos);
        }
    }

    public Task<(UserAccount User, LoyaltyAccount Loyalty)> RegisterGuestCustomerAsync(
        Guid tenantId,
        string fullName,
        string phone,
        string? email = null)
    {
        lock (_lock)
        {
            var existingUser = _users.FirstOrDefault(u => u.Phone == phone);
            if (existingUser != null)
            {
                var existingLoyalty = _loyaltyAccounts.FirstOrDefault(l => l.UserId == existingUser.Id)
                    ?? new LoyaltyAccount { TenantId = tenantId, UserId = existingUser.Id };

                return Task.FromResult((existingUser, existingLoyalty));
            }

            var newUser = new UserAccount
            {
                Id = Guid.NewGuid(),
                FullName = fullName,
                Phone = phone,
                Email = email ?? $"{phone}@guest.aicos.vn",
                Status = "ACTIVE",
                CreatedAt = DateTime.UtcNow
            };
            _users.Add(newUser);

            var newLoyalty = new LoyaltyAccount
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                UserId = newUser.Id,
                PointsBalance = 10, // Tặng 10 điểm chào mừng
                Tier = "STANDARD"
            };
            _loyaltyAccounts.Add(newLoyalty);

            return Task.FromResult((newUser, newLoyalty));
        }
    }
}
