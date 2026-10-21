using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;
using AICOS.Ordering.Api.Domain.Entities;
using AICOS.Ordering.Api.Domain.Enums;
using AICOS.Ordering.Api.Domain.Interfaces;
using AICOS.Ordering.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace AICOS.Ordering.Api.Features;

public static class OrderEndpoints
{
    public static void MapOrderingEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/orders").WithTags("Orders");

        // -------------------------------------------------------------
        // UC_POS_01: Tạo đơn hàng tại quầy
        // -------------------------------------------------------------
        group.MapPost("/", async (
            [FromBody] CreateOrderRequest request,
            [FromServices] IOrderRepository repository,
            [FromServices] IMenuCatalogServiceClient menuCatalogClient,
            [FromServices] ICustomerEngagementServiceClient customerClient,
            [FromServices] ICurrentUser currentUser,
            [FromServices] IEventBus? eventBus,
            CancellationToken ct) =>
        {
            // 1. Kiểm tra tính hợp lệ dữ liệu đầu vào (E1)
            if (request.Items == null || request.Items.Count == 0)
            {
                return Results.BadRequest(new { ErrorCode = "E1", Message = "Đơn hàng phải có ít nhất 1 món ăn/thức uống." });
            }

            foreach (var item in request.Items)
            {
                if (item.Quantity <= 0)
                {
                    return Results.BadRequest(new { ErrorCode = "E1", Message = $"Món '{item.ProductName}' có số lượng không hợp lệ (phải > 0)." });
                }
            }

            // 2. Xác định chi nhánh & nhân viên: lấy từ JWT nếu có, fallback từ request body
            var branchId = currentUser.BranchId ?? (request.BranchId != Guid.Empty ? request.BranchId : Guid.NewGuid());
            var cashierId = currentUser.UserId ?? request.CashierId;

            // 3. Kiểm tra tính sẵn sàng của món trong Menu (cờ IsAvailable do Quản lý bật/tắt)
            var menuItems = request.Items.Select(i => (i.ProductId, i.ProductName)).ToList();
            var menuCheck = await menuCatalogClient.CheckItemsAvailabilityAsync(branchId, menuItems, ct);

            if (!menuCheck.AllAvailable)
            {
                return Results.BadRequest(new
                {
                    ErrorCode = "MENU_ITEM_UNAVAILABLE",
                    Message = menuCheck.Message,
                    UnavailableItems = menuCheck.UnavailableItems
                });
            }

            // 4. Khởi tạo Order Domain Entity
            var order = new Order(
                branchId: branchId,
                type: request.Type,
                cashierId: cashierId,
                customerId: request.CustomerId,
                tableId: request.TableId,
                tableNumber: request.TableNumber,
                deliveryAddress: request.DeliveryAddress,
                paymentMethod: request.PaymentMethod,
                note: request.Note
            );

            foreach (var item in request.Items)
            {
                order.AddItem(item.ProductId, item.ProductName, item.UnitPrice, item.Quantity, item.Note);
            }

            // 5. Alternative Flow A1: Áp dụng mã khuyến mãi (nếu có)
            if (!string.IsNullOrWhiteSpace(request.PromotionCode))
            {
                var voucherResult = await customerClient.ValidateVoucherAsync(request.PromotionCode, order.OriginalAmount, ct);
                if (voucherResult.IsValid)
                {
                    order.ApplyPromotion(request.PromotionCode, voucherResult.DiscountAmount);
                }
                else
                {
                    return Results.BadRequest(new
                    {
                        ErrorCode = "VOUCHER_INVALID",
                        Message = voucherResult.Message
                    });
                }
            }

            // 6. Lưu vào Repository
            await repository.AddAsync(order);

            // 7. Bắn Integration Event OrderCreated sang EventBus
            if (eventBus != null)
            {
                try
                {
                    var eventItems = order.Items
                        .Select(i => new OrderItemDto(i.ProductId, i.ProductName, i.UnitPrice, i.Quantity, i.Note))
                        .ToList();

                    var orderCreatedEvent = new OrderCreatedIntegrationEvent(
                        OrderId: order.Id,
                        BranchId: order.BranchId,
                        CashierId: order.CashierId,
                        CustomerId: order.CustomerId,
                        TableId: order.TableId,
                        OrderType: order.Type.ToString(),
                        TotalAmount: order.FinalAmount,
                        Items: eventItems,
                        CreatedAtUtc: order.CreatedAtUtc
                    );

                    await eventBus.PublishAsync(orderCreatedEvent, ct);
                }
                catch (Exception)
                {
                    // Tránh làm fail request tạo đơn nếu EventBus (Redis) chưa khởi động
                }
            }

            // 8. Trả về kết quả
            return Results.Created($"/api/v1/orders/{order.Id}", new
            {
                Message = "Tạo đơn hàng thành công.",
                OrderId = order.Id,
                OrderCode = order.OrderCode,
                Type = order.Type.ToString(),
                TableNumber = order.TableNumber,
                OriginalAmount = order.OriginalAmount,
                DiscountAmount = order.DiscountAmount,
                PromotionCode = order.PromotionCode,
                FinalAmount = order.FinalAmount,
                Status = order.Status.ToString(),
                PaymentStatus = order.PaymentStatus.ToString(),
                PaymentMethod = order.PaymentMethod?.ToString(),
                CreatedAtUtc = order.CreatedAtUtc
            });
        });

        // -------------------------------------------------------------
        // UC_POS_01 (Thanh toán): Xử lý thanh toán tại quầy
        // -------------------------------------------------------------
        group.MapPost("/{id:guid}/pay", async (
            Guid id,
            [FromBody] ProcessPaymentRequest request,
            [FromServices] IOrderRepository repository,
            [FromServices] IEventBus? eventBus,
            CancellationToken ct) =>
        {
            var order = await repository.GetByIdAsync(id);
            if (order == null)
            {
                return Results.NotFound(new { Message = $"Không tìm thấy đơn hàng với ID: {id}" });
            }

            try
            {
                order.MarkAsPaid(request.PaymentMethod);
                await repository.UpdateAsync(order);

                // Bắn event PaymentCompleted nếu có EventBus
                if (eventBus != null)
                {
                    try
                    {
                        await eventBus.PublishAsync(new PaymentCompletedIntegrationEvent(
                            PaymentId: Guid.NewGuid(),
                            OrderId: order.Id,
                            Amount: order.FinalAmount,
                            PaymentMethod: request.PaymentMethod.ToString(),
                            PaidAtUtc: DateTime.UtcNow
                        ), ct);
                    }
                    catch (Exception)
                    {
                        // Bỏ qua lỗi Redis khi chạy mock local
                    }
                }

                return Results.Ok(new
                {
                    Message = "Thanh toán thành công.",
                    OrderId = order.Id,
                    OrderCode = order.OrderCode,
                    PaymentStatus = order.PaymentStatus.ToString(),
                    PaymentMethod = order.PaymentMethod.ToString(),
                    Status = order.Status.ToString(),
                    FinalAmount = order.FinalAmount
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { Message = ex.Message });
            }
        });

        // -------------------------------------------------------------
        // UC_POS_02: In / Gửi hóa đơn
        // -------------------------------------------------------------
        group.MapGet("/{id:guid}/receipt", async (
            Guid id,
            [FromServices] IOrderRepository repository) =>
        {
            var order = await repository.GetByIdAsync(id);
            if (order == null)
            {
                return Results.NotFound(new { Message = $"Không tìm thấy đơn hàng với ID: {id}" });
            }

            // Ghi nhận đã in và tăng biến đếm số lần in
            order.MarkAsPrinted();
            await repository.UpdateAsync(order);

            var receipt = new ReceiptResponse(
                ReceiptNumber: order.OrderCode,
                OrderId: order.Id,
                BranchId: order.BranchId,
                CashierId: order.CashierId,
                OrderType: order.Type.ToString(),
                TableNumber: order.TableNumber,
                DeliveryAddress: order.DeliveryAddress,
                CreatedAtUtc: order.CreatedAtUtc,
                Items: order.Items.Select(i => new ReceiptItemDto(
                    i.ProductName,
                    i.UnitPrice,
                    i.Quantity,
                    i.TotalPrice,
                    i.Note
                )).ToList(),
                OriginalAmount: order.OriginalAmount,
                PromotionCode: order.PromotionCode,
                DiscountAmount: order.DiscountAmount,
                FinalAmount: order.FinalAmount,
                PaymentStatus: order.PaymentStatus.ToString(),
                PaymentMethod: order.PaymentMethod?.ToString() ?? "Chưa xác định",
                IsReprint: order.PrintedCount > 1,
                PrintedCount: order.PrintedCount,
                PrintedAtUtc: order.LastPrintedAtUtc ?? DateTime.UtcNow
            );

            return Results.Ok(receipt);
        });

        // In lại hóa đơn theo mã đơn hàng (Nghiệp vụ phụ của UC_POS_02)
        group.MapGet("/by-code/{orderCode}/receipt", async (
            string orderCode,
            [FromServices] IOrderRepository repository) =>
        {
            var order = await repository.GetByOrderCodeAsync(orderCode);
            if (order == null)
            {
                return Results.NotFound(new { Message = $"Không tìm thấy đơn hàng với mã: {orderCode}" });
            }

            order.MarkAsPrinted();
            await repository.UpdateAsync(order);

            var receipt = new ReceiptResponse(
                ReceiptNumber: order.OrderCode,
                OrderId: order.Id,
                BranchId: order.BranchId,
                CashierId: order.CashierId,
                OrderType: order.Type.ToString(),
                TableNumber: order.TableNumber,
                DeliveryAddress: order.DeliveryAddress,
                CreatedAtUtc: order.CreatedAtUtc,
                Items: order.Items.Select(i => new ReceiptItemDto(
                    i.ProductName,
                    i.UnitPrice,
                    i.Quantity,
                    i.TotalPrice,
                    i.Note
                )).ToList(),
                OriginalAmount: order.OriginalAmount,
                PromotionCode: order.PromotionCode,
                DiscountAmount: order.DiscountAmount,
                FinalAmount: order.FinalAmount,
                PaymentStatus: order.PaymentStatus.ToString(),
                PaymentMethod: order.PaymentMethod?.ToString() ?? "Chưa xác định",
                IsReprint: order.PrintedCount > 1,
                PrintedCount: order.PrintedCount,
                PrintedAtUtc: order.LastPrintedAtUtc ?? DateTime.UtcNow
            );

            return Results.Ok(receipt);
        });

        // -------------------------------------------------------------
        // UC_POS_03: Quản lý bàn / Mang đi / Giao hàng
        // -------------------------------------------------------------
        group.MapPut("/{id:guid}/service-type", async (
            Guid id,
            [FromBody] SwitchServiceTypeRequest request,
            [FromServices] IOrderRepository repository) =>
        {
            var order = await repository.GetByIdAsync(id);
            if (order == null)
            {
                return Results.NotFound(new { Message = $"Không tìm thấy đơn hàng với ID: {id}" });
            }

            try
            {
                order.SwitchServiceType(request.Type, request.TableId, request.TableNumber, request.DeliveryAddress);
                await repository.UpdateAsync(order);

                return Results.Ok(new
                {
                    Message = "Cập nhật hình thức phục vụ thành công.",
                    OrderId = order.Id,
                    OrderType = order.Type.ToString(),
                    TableId = order.TableId,
                    TableNumber = order.TableNumber,
                    DeliveryAddress = order.DeliveryAddress
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { Message = ex.Message });
            }
        });

        // -------------------------------------------------------------
        // UC_POS_04: Hủy đơn hàng (Quy tắc BR-04)
        // -------------------------------------------------------------
        group.MapPost("/{id:guid}/cancel", async (
            Guid id,
            [FromBody] CancelOrderRequest request,
            [FromServices] IOrderRepository repository) =>
        {
            var order = await repository.GetByIdAsync(id);
            if (order == null)
            {
                return Results.NotFound(new { Message = $"Không tìm thấy đơn hàng với ID: {id}" });
            }

            try
            {
                // BR-04: Đơn hàng chỉ được hủy khi trạng thái chưa "Đã thanh toán"
                order.CancelOrder(request.Reason);
                await repository.UpdateAsync(order);

                return Results.Ok(new
                {
                    Message = "Đơn hàng đã được hủy thành công.",
                    OrderId = order.Id,
                    Status = order.Status.ToString(),
                    CancelledReason = order.CancelledReason,
                    CancelledAtUtc = order.CancelledAtUtc
                });
            }
            catch (InvalidOperationException ex)
            {
                // Xử lý vi phạm BR-04 hoặc trạng thái không hợp lệ
                return Results.BadRequest(new
                {
                    ErrorCode = "BR-04",
                    Message = ex.Message
                });
            }
        });

        // -------------------------------------------------------------
        // API Truy vấn: Lấy danh sách & Chi tiết
        // -------------------------------------------------------------
        group.MapGet("/", async ([FromServices] IOrderRepository repository) =>
        {
            var orders = await repository.GetAllAsync();
            var response = orders.Select(o => new
            {
                o.Id,
                o.OrderCode,
                Type = o.Type.ToString(),
                Status = o.Status.ToString(),
                PaymentStatus = o.PaymentStatus.ToString(),
                PaymentMethod = o.PaymentMethod?.ToString(),
                TableNumber = o.TableNumber,
                o.OriginalAmount,
                o.DiscountAmount,
                o.FinalAmount,
                ItemCount = o.Items.Count,
                o.CreatedAtUtc
            });

            return Results.Ok(response);
        });

        group.MapGet("/{id:guid}", async (Guid id, [FromServices] IOrderRepository repository) =>
        {
            var order = await repository.GetByIdAsync(id);
            if (order == null)
            {
                return Results.NotFound(new { Message = $"Không tìm thấy đơn hàng với ID: {id}" });
            }

            return Results.Ok(new
            {
                order.Id,
                order.OrderCode,
                order.BranchId,
                order.CashierId,
                order.CustomerId,
                Type = order.Type.ToString(),
                Status = order.Status.ToString(),
                PaymentStatus = order.PaymentStatus.ToString(),
                PaymentMethod = order.PaymentMethod?.ToString(),
                order.TableId,
                order.TableNumber,
                order.DeliveryAddress,
                order.Note,
                order.OriginalAmount,
                order.PromotionCode,
                order.DiscountAmount,
                order.FinalAmount,
                order.CreatedAtUtc,
                order.CompletedAtUtc,
                order.CancelledAtUtc,
                order.CancelledReason,
                Items = order.Items.Select(i => new
                {
                    i.Id,
                    i.ProductId,
                    i.ProductName,
                    i.UnitPrice,
                    i.Quantity,
                    i.TotalPrice,
                    i.Note
                })
            });
        });
    }
}

// -------------------------------------------------------------
// DTOs (Data Transfer Objects)
// -------------------------------------------------------------

public class CreateOrderRequest
{
    public Guid BranchId { get; set; }
    public Guid? CashierId { get; set; }
    public Guid? CustomerId { get; set; }
    public Guid? TableId { get; set; }
    public string? TableNumber { get; set; }
    public string? DeliveryAddress { get; set; }
    public OrderType Type { get; set; } = OrderType.DineIn;
    public PaymentMethod? PaymentMethod { get; set; }
    public string? PromotionCode { get; set; }
    public string? Note { get; set; }
    public List<OrderItemRequest> Items { get; set; } = new();
}

public class OrderItemRequest
{
    public Guid ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public decimal UnitPrice { get; set; }
    public int Quantity { get; set; }
    public string? Note { get; set; }
}

public class ProcessPaymentRequest
{
    public PaymentMethod PaymentMethod { get; set; } = PaymentMethod.Cash;
}

public class SwitchServiceTypeRequest
{
    public OrderType Type { get; set; }
    public Guid? TableId { get; set; }
    public string? TableNumber { get; set; }
    public string? DeliveryAddress { get; set; }
}

public class CancelOrderRequest
{
    public string Reason { get; set; } = string.Empty;
}

public record ReceiptItemDto(
    string ProductName,
    decimal UnitPrice,
    int Quantity,
    decimal TotalPrice,
    string? Note);

public record ReceiptResponse(
    string ReceiptNumber,
    Guid OrderId,
    Guid BranchId,
    Guid? CashierId,
    string OrderType,
    string? TableNumber,
    string? DeliveryAddress,
    DateTime CreatedAtUtc,
    IReadOnlyList<ReceiptItemDto> Items,
    decimal OriginalAmount,
    string? PromotionCode,
    decimal DiscountAmount,
    decimal FinalAmount,
    string PaymentStatus,
    string PaymentMethod,
    bool IsReprint,
    int PrintedCount,
    DateTime PrintedAtUtc);
