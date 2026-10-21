namespace AICOS.KitchenOperations.Api.Features;

public record KitchenTicketItemDto(
    Guid ProductId,
    string ProductName,
    int Quantity,
    string? Note);

public record CupStickerDto(
    Guid StickerId,
    Guid TicketId,
    Guid OrderId,
    string OrderCode,
    string? TableNumber,
    string ProductName,
    int CupIndex,
    int TotalCupsInOrder,
    string? Customizations,
    DateTime PrintedAtUtc);

public record KitchenTicketDto(
    Guid Id,
    Guid OrderId,
    string OrderCode,
    Guid BranchId,
    string? TableNumber,
    string OrderType,
    string Status,
    bool IsPaid,
    bool IsPriority,
    DateTime CreatedAtUtc,
    DateTime? ScheduledPickupTimeUtc,
    DateTime? BrewingStartedAtUtc,
    DateTime? ReadyAtUtc,
    int TotalCups,
    IReadOnlyList<KitchenTicketItemDto> Items,
    IReadOnlyList<CupStickerDto> Stickers);

public record BatchCupItemDto(
    Guid TicketId,
    string OrderCode,
    string? TableNumber,
    string? Note,
    DateTime CreatedAtUtc);

/// <summary>
/// Mẻ pha chế gộp danh nghĩa (BR-42: cửa sổ 5 phút, tối đa 4 ly/mẻ)
/// </summary>
public record BrewingBatchDto(
    string ProductName,
    int TotalQuantity,
    int MaxBatchCapacity,
    IReadOnlyList<BatchCupItemDto> Cups);

/// <summary>
/// Thông báo đơn sẵn sàng phục vụ real-time cho Thu ngân / Server (UC_POS_07)
/// </summary>
public record OrderReadyNotificationDto(
    Guid TicketId,
    Guid OrderId,
    string OrderCode,
    Guid BranchId,
    string? TableNumber,
    string OrderType,
    int TotalCups,
    DateTime ReadyAtUtc,
    string Message);
