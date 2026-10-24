using AICOS.BuildingBlocks.Messaging;
using AICOS.Contracts.Events;
using AICOS.Ordering.Api.Domain.Interfaces;
using AICOS.Ordering.Api.Persistence;
using AICOS.Ordering.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace AICOS.Ordering.Api.Features;

public static class QrGuestEndpoints
{
    public static void MapQrGuestEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/qr").WithTags("Guest QR Ordering");

        // -------------------------------------------------------------
        // SCRUM-66: Quét QR & xác thực token bàn/chi nhánh
        // -------------------------------------------------------------
        group.MapPost("/scan", async (
            [FromBody] ScanQrRequest request,
            [FromServices] IQrOrderingRepository qrRepo) =>
        {
            if (string.IsNullOrWhiteSpace(request.QrToken))
            {
                return Results.BadRequest(new { Message = "Mã qrToken không được để trống." });
            }

            // 1. Kiểm tra bàn theo QR Token
            var (table, area) = await qrRepo.GetTableByQrTokenAsync(request.QrToken);
            if (table == null)
            {
                return Results.NotFound(new { Message = "Mã QR bàn không hợp lệ hoặc đã bị vô hiệu hóa." });
            }

            var branchId = area?.BranchId ?? FakeQrOrderingRepository.DefaultBranchId;

            // 2. Lấy hoặc mở phiên bàn (Table Session)
            var session = await qrRepo.OpenSessionForTableAsync(branchId, table.Id);

            return Results.Ok(new
            {
                Message = "Xác thực mã QR bàn thành công.",
                SessionId = session.Id,
                BranchId = branchId,
                TableId = table.Id,
                TableNumber = table.TableNumber,
                AreaName = area?.Name ?? "Khu vực chung",
                Capacity = table.Capacity,
                OpenedAt = session.OpenedAt,
                // Trả về token định danh phiên cho Web App khách
                GuestSessionToken = $"GUEST_SESSION_{session.Id}_{table.Id}"
            });
        });

        // -------------------------------------------------------------
        // SCRUM-70, SCRUM-71: Lấy Menu số & danh mục cho khách tại bàn
        // Chỉ trả về các món đang mở bán (is_available = true)
        // -------------------------------------------------------------
        group.MapGet("/menu", async (
            [FromQuery] Guid? branchId,
            [FromServices] IQrOrderingRepository qrRepo) =>
        {
            var targetBranch = branchId ?? FakeQrOrderingRepository.DefaultBranchId;
            var categories = await qrRepo.GetMenuByBranchIdAsync(targetBranch);

            return Results.Ok(new
            {
                BranchId = targetBranch,
                TotalCategories = categories.Count,
                Categories = categories
            });
        });

        // -------------------------------------------------------------
        // SCRUM-67: Giỏ hàng gắn với Table Session (Backend ↔ Redis)
        // -------------------------------------------------------------
        group.MapGet("/cart", async (
            [FromQuery] Guid sessionId,
            [FromServices] IRedisCartService cartService) =>
        {
            if (sessionId == Guid.Empty)
            {
                return Results.BadRequest(new { Message = "Vui lòng cung cấp sessionId." });
            }

            var cart = await cartService.GetCartAsync(sessionId);
            return Results.Ok(cart);
        });

        group.MapPost("/cart/items", async (
            [FromQuery] Guid sessionId,
            [FromBody] AddCartItemRequest request,
            [FromServices] IRedisCartService cartService) =>
        {
            if (sessionId == Guid.Empty)
            {
                return Results.BadRequest(new { Message = "Vui lòng cung cấp sessionId." });
            }

            var updatedCart = await cartService.AddItemAsync(sessionId, request);
            return Results.Ok(new
            {
                Message = $"Đã thêm '{request.ProductName}' vào giỏ hàng bàn.",
                Cart = updatedCart
            });
        });

        group.MapPut("/cart/items/{itemId:guid}", async (
            [FromQuery] Guid sessionId,
            Guid itemId,
            [FromBody] UpdateCartQuantityRequest request,
            [FromServices] IRedisCartService cartService) =>
        {
            if (sessionId == Guid.Empty)
            {
                return Results.BadRequest(new { Message = "Vui lòng cung cấp sessionId." });
            }

            var updatedCart = await cartService.UpdateItemQuantityAsync(sessionId, itemId, request.Quantity);
            return Results.Ok(updatedCart);
        });

        group.MapDelete("/cart/items/{itemId:guid}", async (
            [FromQuery] Guid sessionId,
            Guid itemId,
            [FromServices] IRedisCartService cartService) =>
        {
            if (sessionId == Guid.Empty)
            {
                return Results.BadRequest(new { Message = "Vui lòng cung cấp sessionId." });
            }

            var updatedCart = await cartService.RemoveItemAsync(sessionId, itemId);
            return Results.Ok(updatedCart);
        });

        group.MapDelete("/cart", async (
            [FromQuery] Guid sessionId,
            [FromServices] IRedisCartService cartService) =>
        {
            if (sessionId == Guid.Empty)
            {
                return Results.BadRequest(new { Message = "Vui lòng cung cấp sessionId." });
            }

            await cartService.ClearCartAsync(sessionId);
            return Results.Ok(new { Message = "Đã dọn sạch giỏ hàng của bàn." });
        });

        // -------------------------------------------------------------
        // SCRUM-117: Revalidate giá, tùy chọn, khả dụng và tính tiền phía Server
        // -------------------------------------------------------------
        group.MapPost("/cart/revalidate", async (
            [FromQuery] Guid sessionId,
            [FromQuery] Guid? branchId,
            [FromServices] IRedisCartService cartService,
            [FromServices] IQrOrderingRepository qrRepo) =>
        {
            if (sessionId == Guid.Empty)
            {
                return Results.BadRequest(new { Message = "Vui lòng cung cấp sessionId." });
            }

            var cart = await cartService.GetCartAsync(sessionId);
            var targetBranch = branchId ?? FakeQrOrderingRepository.DefaultBranchId;

            var validationResult = await qrRepo.ValidateAndCalculateCartAsync(targetBranch, cart.Items);

            return Results.Ok(validationResult);
        });

        // -------------------------------------------------------------
        // SCRUM-116, SCRUM-68: Đặt món (Đợt 1 / Gọi thêm đợt 2, 3)
        // -------------------------------------------------------------
        group.MapPost("/checkout", async (
            [FromBody] QrCheckoutRequest request,
            [FromServices] IRedisCartService cartService,
            [FromServices] IQrOrderingRepository qrRepo,
            [FromServices] IEventBus? eventBus,
            CancellationToken ct) =>
        {
            if (request.SessionId == Guid.Empty)
            {
                return Results.BadRequest(new { Message = "Vui lòng cung cấp sessionId hợp lệ." });
            }

            // 1. Kiểm tra session có tồn tại và đang mở không
            var session = await qrRepo.GetSessionByIdAsync(request.SessionId);
            if (session == null || session.Status != "OPEN")
            {
                return Results.BadRequest(new { Message = "Phiên bàn này không tồn tại hoặc đã bị đóng." });
            }

            // 2. Lấy giỏ hàng từ Redis
            var cart = await cartService.GetCartAsync(request.SessionId, ct);
            if (cart.Items.Count == 0)
            {
                return Results.BadRequest(new { Message = "Giỏ hàng hiện đang rỗng, vui lòng chọn món trước khi đặt." });
            }

            // 3. SCRUM-117: Chạy Revalidate giá & khả dụng phía server
            var validation = await qrRepo.ValidateAndCalculateCartAsync(session.BranchId, cart.Items);
            if (!validation.IsValid)
            {
                return Results.BadRequest(new
                {
                    ErrorCode = "CART_VALIDATION_FAILED",
                    Message = "Một số món trong giỏ hàng đã thay đổi hoặc hết hàng. Vui lòng kiểm tra lại.",
                    Errors = validation.Errors
                });
            }

            // 4. Tạo Order và lưu vào Database
            var order = await qrRepo.CreateOrderFromCartAsync(
                branchId: session.BranchId,
                sessionId: session.Id,
                customerId: request.CustomerId,
                validatedCart: validation,
                note: request.Note
            );

            // 5. Xóa giỏ hàng trong Redis sau khi đặt món thành công
            await cartService.ClearCartAsync(request.SessionId, ct);

            // 6. Bắn Event sang Kitchen Operations để bếp nhận đơn làm món
            if (eventBus != null)
            {
                try
                {
                    var eventItems = order.Items.Select(i => new OrderItemDto(
                        ProductId: i.ProductId,
                        ProductName: i.ProductName,
                        UnitPrice: i.UnitPrice,
                        Quantity: i.Quantity,
                        Note: i.Note
                    )).ToList();

                    var createdEvent = new OrderCreatedIntegrationEvent(
                        OrderId: order.Id,
                        BranchId: order.BranchId,
                        CashierId: null,
                        CustomerId: order.CustomerId,
                        TableId: order.TableId,
                        OrderType: order.Type.ToString(),
                        TotalAmount: order.TotalAmount,
                        Items: eventItems,
                        CreatedAtUtc: order.CreatedAtUtc
                    );

                    await eventBus.PublishAsync(createdEvent, ct);
                }
                catch { }
            }

            return Results.Created($"/api/v1/orders/{order.Id}", new
            {
                Message = "Đặt món thành công! Đơn hàng đã được chuyển tới quầy pha chế.",
                OrderId = order.Id,
                OrderCode = order.OrderCode,
                SessionId = session.Id,
                TableNumber = order.TableNumber,
                TotalAmount = order.TotalAmount,
                Status = order.Status.ToString(),
                CreatedAt = order.CreatedAtUtc
            });
        });

        // -------------------------------------------------------------
        // SCRUM-68: Lấy Hóa đơn tổng của bàn (tất cả các đợt gọi món)
        // -------------------------------------------------------------
        group.MapGet("/session/bill", async (
            [FromQuery] Guid sessionId,
            [FromServices] IQrOrderingRepository qrRepo) =>
        {
            if (sessionId == Guid.Empty)
            {
                return Results.BadRequest(new { Message = "Vui lòng cung cấp sessionId." });
            }

            var billSummary = await qrRepo.GetSessionBillAsync(sessionId);
            if (billSummary == null)
            {
                return Results.NotFound(new { Message = "Không tìm thấy phiên bàn hoặc hóa đơn." });
            }

            return Results.Ok(billSummary);
        });

        // -------------------------------------------------------------
        // SCRUM-70: Theo dõi trạng thái đơn hàng của bàn (Real-time tracking)
        // -------------------------------------------------------------
        group.MapGet("/orders/status", async (
            [FromQuery] Guid sessionId,
            [FromServices] IQrOrderingRepository qrRepo) =>
        {
            if (sessionId == Guid.Empty)
            {
                return Results.BadRequest(new { Message = "Vui lòng cung cấp sessionId." });
            }

            var orders = await qrRepo.GetSessionOrdersAsync(sessionId);
            return Results.Ok(orders);
        });

        // -------------------------------------------------------------
        // SCRUM-69: Guest đăng ký thành viên (Sign-up) để tích điểm
        // -------------------------------------------------------------
        group.MapPost("/customer/sign-up", async (
            [FromBody] GuestSignUpRequest request,
            [FromServices] IQrOrderingRepository qrRepo) =>
        {
            if (string.IsNullOrWhiteSpace(request.Phone) || string.IsNullOrWhiteSpace(request.FullName))
            {
                return Results.BadRequest(new { Message = "Họ tên và Số điện thoại là bắt buộc." });
            }

            var tenantId = request.TenantId != Guid.Empty ? request.TenantId : FakeQrOrderingRepository.DefaultTenantId;

            var (user, loyalty) = await qrRepo.RegisterGuestCustomerAsync(
                tenantId: tenantId,
                fullName: request.FullName.Trim(),
                phone: request.Phone.Trim(),
                email: request.Email?.Trim()
            );

            return Results.Ok(new
            {
                Message = "Đăng ký thành viên thành công! Bạn đã được cộng 10 điểm chào mừng.",
                CustomerId = user.Id,
                FullName = user.FullName,
                Phone = user.Phone,
                Email = user.Email,
                LoyaltyTier = loyalty.Tier,
                PointsBalance = loyalty.PointsBalance
            });
        });
    }
}

// -------------------------------------------------------------
// Request DTOs
// -------------------------------------------------------------

public class ScanQrRequest
{
    public string QrToken { get; set; } = string.Empty;
}

public class UpdateCartQuantityRequest
{
    public int Quantity { get; set; }
}

public class QrCheckoutRequest
{
    public Guid SessionId { get; set; }
    public Guid? CustomerId { get; set; }
    public string? Note { get; set; }
}

public class GuestSignUpRequest
{
    public Guid TenantId { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string? Email { get; set; }
}
