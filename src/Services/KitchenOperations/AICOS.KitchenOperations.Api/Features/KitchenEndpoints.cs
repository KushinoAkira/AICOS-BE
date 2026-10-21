using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;
using AICOS.KitchenOperations.Api.Domain.Entities;
using AICOS.KitchenOperations.Api.Domain.Enums;
using AICOS.KitchenOperations.Api.Domain.Interfaces;
using AICOS.KitchenOperations.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace AICOS.KitchenOperations.Api.Features;

public static class KitchenEndpoints
{
    public static void MapKitchenOperationsEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/kitchen").WithTags("Kitchen Operations");

        // -------------------------------------------------------------
        // UC_KITCHEN_01: Lấy hàng đợi pha chế (FIFO + Priority - BR-41)
        // Hỗ trợ NFR-34 (Offline cache / Initial sync khi kết nối lại)
        // -------------------------------------------------------------
        group.MapGet("/queue", async (
            [FromQuery] Guid? branchId,
            [FromServices] IKitchenTicketRepository repository,
            [FromServices] ICurrentUser currentUser) =>
        {
            var targetBranchId = branchId ?? currentUser.BranchId ?? Guid.Empty;
            if (targetBranchId == Guid.Empty)
            {
                return Results.BadRequest(new { Message = "Vui lòng cung cấp branchId hoặc đăng nhập tài khoản có gán chi nhánh." });
            }

            var tickets = await repository.GetQueueAsync(targetBranchId);
            var dtos = tickets.Select(MapToDto).ToList();

            return Results.Ok(dtos);
        });

        // -------------------------------------------------------------
        // UC_KITCHEN_01: Lấy danh sách mẻ pha chế gộp tự động (BR-42)
        // Cửa sổ 5 phút, tối đa 4 ly/mẻ, gộp danh nghĩa theo tên món
        // -------------------------------------------------------------
        group.MapGet("/batches", async (
            [FromQuery] Guid? branchId,
            [FromServices] IKitchenTicketRepository repository,
            [FromServices] ICurrentUser currentUser) =>
        {
            var targetBranchId = branchId ?? currentUser.BranchId ?? Guid.Empty;
            if (targetBranchId == Guid.Empty)
            {
                return Results.BadRequest(new { Message = "Vui lòng cung cấp branchId hoặc đăng nhập tài khoản có gán chi nhánh." });
            }

            var batches = await repository.GetActiveBatchesAsync(targetBranchId, TimeSpan.FromMinutes(5), maxBatchSize: 4);
            return Results.Ok(batches);
        });

        // -------------------------------------------------------------
        // UC_KITCHEN_01 & UC_KITCHEN_02: Tiếp nhận đơn hàng vào khu vực pha chế
        // -------------------------------------------------------------
        group.MapPost("/tickets", async (
            [FromBody] CreateKitchenTicketRequest request,
            [FromServices] IKitchenTicketRepository repository,
            [FromServices] IKitchenNotifier notifier,
            [FromServices] ICurrentUser currentUser,
            CancellationToken ct) =>
        {
            if (request.Items == null || request.Items.Count == 0)
            {
                return Results.BadRequest(new { ErrorCode = "E1", Message = "Đơn pha chế phải có ít nhất 1 món." });
            }

            var branchId = request.BranchId != Guid.Empty ? request.BranchId : (currentUser.BranchId ?? Guid.NewGuid());

            var ticket = new KitchenTicket(
                orderId: request.OrderId,
                orderCode: request.OrderCode,
                branchId: branchId,
                tableNumber: request.TableNumber,
                orderType: request.OrderType,
                isPaid: request.IsPaid,
                scheduledPickupTimeUtc: request.ScheduledPickupTimeUtc
            );

            foreach (var item in request.Items)
            {
                ticket.AddItem(item.ProductId, item.ProductName, item.Quantity, item.Note);
            }

            // BR-43: Tự động in tem ly nước nếu là Dine-in hoặc đã thanh toán (Online)
            bool stickersGenerated = ticket.TryGenerateCupStickers(out var stickers);

            await repository.AddAsync(ticket);

            // Gửi WebSocket Real-time tới màn hình KDS (< 300ms theo NFR-28)
            var ticketDto = MapToDto(ticket);
            await notifier.NotifyNewTicketAsync(branchId, ticketDto, ct);

            if (stickersGenerated && stickers.Count > 0)
            {
                var stickersDto = stickers.Select(MapStickerToDto).ToList();
                await notifier.NotifyStickersPrintedAsync(branchId, stickersDto, ct);
            }

            return Results.Created($"/api/v1/kitchen/tickets/{ticket.Id}", ticketDto);
        });

        // -------------------------------------------------------------
        // UC_KITCHEN_01: Cập nhật trạng thái pha chế (Pending -> Brewing -> Ready for Pickup)
        // & UC_POS_07: Thông báo đơn sẵn sàng phục vụ khách qua WebSocket
        // -------------------------------------------------------------
        group.MapPut("/tickets/{id:guid}/status", async (
            Guid id,
            [FromBody] UpdateKitchenTicketStatusRequest request,
            [FromServices] IKitchenTicketRepository repository,
            [FromServices] IKitchenNotifier notifier,
            [FromServices] IEventBus? eventBus,
            CancellationToken ct) =>
        {
            var ticket = await repository.GetByIdAsync(id);
            if (ticket == null)
            {
                return Results.NotFound(new { Message = $"Không tìm thấy đơn pha chế với ID: {id}" });
            }

            try
            {
                switch (request.Status)
                {
                    case KitchenTicketStatus.Brewing:
                        ticket.StartBrewing();
                        break;

                    case KitchenTicketStatus.ReadyForPickup:
                        // Cấp độ đơn hàng: Hoàn tất tất cả món trong đơn
                        ticket.MarkReadyForPickup();

                        // UC_POS_07: Bắn thông báo real-time qua SignalR tới quầy Thu ngân & Phục vụ
                        var readyNotification = new OrderReadyNotificationDto(
                            TicketId: ticket.Id,
                            OrderId: ticket.OrderId,
                            OrderCode: ticket.OrderCode,
                            BranchId: ticket.BranchId,
                            TableNumber: ticket.TableNumber,
                            OrderType: ticket.OrderType,
                            TotalCups: ticket.TotalCups,
                            ReadyAtUtc: ticket.ReadyAtUtc ?? DateTime.UtcNow,
                            Message: string.IsNullOrWhiteSpace(ticket.TableNumber)
                                ? $"Đơn hàng {ticket.OrderCode} ({ticket.OrderType}) đã pha chế xong!"
                                : $"Đơn hàng {ticket.OrderCode} tại Bàn {ticket.TableNumber} đã sẵn sàng phục vụ!"
                        );

                        await notifier.NotifyOrderReadyForPickupAsync(ticket.BranchId, readyNotification, ct);

                        // Đồng thời bắn EventBus cho các service khác (nếu có)
                        if (eventBus != null)
                        {
                            try
                            {
                                await eventBus.PublishAsync(new KitchenOrderReadyIntegrationEvent(
                                    TicketId: ticket.Id,
                                    OrderId: ticket.OrderId,
                                    OrderCode: ticket.OrderCode,
                                    BranchId: ticket.BranchId,
                                    TableNumber: ticket.TableNumber,
                                    ReadyAtUtc: ticket.ReadyAtUtc ?? DateTime.UtcNow
                                ), ct);
                            }
                            catch (Exception) { /* Local bypass */ }
                        }
                        break;

                    case KitchenTicketStatus.Completed:
                        ticket.Complete();
                        break;

                    case KitchenTicketStatus.Cancelled:
                        ticket.Cancel();
                        break;

                    default:
                        return Results.BadRequest(new { Message = $"Trạng thái không hợp lệ: {request.Status}" });
                }

                await repository.UpdateAsync(ticket);
                await notifier.NotifyStatusChangedAsync(ticket.BranchId, ticket.Id, ticket.Status.ToString(), ct);

                return Results.Ok(new
                {
                    Message = "Cập nhật trạng thái thành công.",
                    TicketId = ticket.Id,
                    Status = ticket.Status.ToString(),
                    UpdatedAtUtc = DateTime.UtcNow
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { Message = ex.Message });
            }
        });

        // -------------------------------------------------------------
        // UC_KITCHEN_02: In / Lấy danh sách tem dán ly (Cup Sticker)
        // -------------------------------------------------------------
        group.MapGet("/tickets/{id:guid}/stickers", async (
            Guid id,
            [FromServices] IKitchenTicketRepository repository) =>
        {
            var ticket = await repository.GetByIdAsync(id);
            if (ticket == null)
            {
                return Results.NotFound(new { Message = $"Không tìm thấy đơn pha chế với ID: {id}" });
            }

            // Đảm bảo sinh tem nếu đủ điều kiện BR-43
            ticket.TryGenerateCupStickers(out var stickers);
            await repository.UpdateAsync(ticket);

            var dtos = stickers.Select(MapStickerToDto).ToList();
            return Results.Ok(new
            {
                TicketId = ticket.Id,
                OrderCode = ticket.OrderCode,
                TableNumber = ticket.TableNumber,
                TotalStickers = dtos.Count,
                Stickers = dtos
            });
        });

        // -------------------------------------------------------------
        // Kích hoạt sinh tem khi đơn Online được xác nhận thanh toán (BR-43)
        // -------------------------------------------------------------
        group.MapPost("/tickets/by-order/{orderId:guid}/mark-paid", async (
            Guid orderId,
            [FromServices] IKitchenTicketRepository repository,
            [FromServices] IKitchenNotifier notifier,
            CancellationToken ct) =>
        {
            var ticket = await repository.GetByOrderIdAsync(orderId);
            if (ticket == null)
            {
                return Results.NotFound(new { Message = $"Không tìm thấy đơn pha chế cho OrderId: {orderId}" });
            }

            ticket.MarkAsPaid();
            bool generated = ticket.TryGenerateCupStickers(out var stickers);
            await repository.UpdateAsync(ticket);

            if (generated && stickers.Count > 0)
            {
                var stickersDto = stickers.Select(MapStickerToDto).ToList();
                await notifier.NotifyStickersPrintedAsync(ticket.BranchId, stickersDto, ct);
            }

            return Results.Ok(new
            {
                Message = "Đã cập nhật trạng thái thanh toán và kích hoạt in tem ly nước tự động (BR-43).",
                TicketId = ticket.Id,
                IsPaid = ticket.IsPaid,
                StickersCount = ticket.Stickers.Count
            });
        });
    }

    private static KitchenTicketDto MapToDto(KitchenTicket ticket) =>
        new(
            Id: ticket.Id,
            OrderId: ticket.OrderId,
            OrderCode: ticket.OrderCode,
            BranchId: ticket.BranchId,
            TableNumber: ticket.TableNumber,
            OrderType: ticket.OrderType,
            Status: ticket.Status.ToString(),
            IsPaid: ticket.IsPaid,
            IsPriority: ticket.IsPriority,
            CreatedAtUtc: ticket.CreatedAtUtc,
            ScheduledPickupTimeUtc: ticket.ScheduledPickupTimeUtc,
            BrewingStartedAtUtc: ticket.BrewingStartedAtUtc,
            ReadyAtUtc: ticket.ReadyAtUtc,
            TotalCups: ticket.TotalCups,
            Items: ticket.Items.Select(i => new KitchenTicketItemDto(i.ProductId, i.ProductName, i.Quantity, i.Note)).ToList(),
            Stickers: ticket.Stickers.Select(MapStickerToDto).ToList()
        );

    private static CupStickerDto MapStickerToDto(CupSticker sticker) =>
        new(
            StickerId: sticker.Id,
            TicketId: sticker.TicketId,
            OrderId: sticker.OrderId,
            OrderCode: sticker.OrderCode,
            TableNumber: sticker.TableNumber,
            ProductName: sticker.ProductName,
            CupIndex: sticker.CupIndex,
            TotalCupsInOrder: sticker.TotalCupsInOrder,
            Customizations: sticker.Customizations,
            PrintedAtUtc: sticker.PrintedAtUtc
        );
}

// -------------------------------------------------------------
// Request Models
// -------------------------------------------------------------

public class CreateKitchenTicketRequest
{
    public Guid OrderId { get; set; }
    public string OrderCode { get; set; } = string.Empty;
    public Guid BranchId { get; set; }
    public string? TableNumber { get; set; }
    public string OrderType { get; set; } = "DineIn"; // DineIn, Takeaway, Delivery
    public bool IsPaid { get; set; }
    public DateTime? ScheduledPickupTimeUtc { get; set; }
    public List<CreateKitchenTicketItemRequest> Items { get; set; } = new();
}

public class CreateKitchenTicketItemRequest
{
    public Guid ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public string? Note { get; set; }
}

public class UpdateKitchenTicketStatusRequest
{
    public KitchenTicketStatus Status { get; set; }
}
