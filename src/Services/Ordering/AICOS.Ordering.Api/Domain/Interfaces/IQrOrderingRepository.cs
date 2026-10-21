using AICOS.Ordering.Api.Domain.Entities;
using AICOS.Ordering.Api.Services;

namespace AICOS.Ordering.Api.Domain.Interfaces;

public record QrMenuOptionDto(Guid OptionId, string Name, decimal ExtraPrice, bool IsActive);

public record QrMenuOptionGroupDto(
    Guid GroupId,
    string Name,
    int MinSelect,
    int MaxSelect,
    IReadOnlyList<QrMenuOptionDto> Options);

public record QrMenuVariantDto(Guid VariantId, string SizeName, decimal Price, bool IsActive);

public record QrMenuProductDto(
    Guid ProductId,
    string Name,
    string? Description,
    string? ImageUrl,
    bool IsAvailable,
    IReadOnlyList<QrMenuVariantDto> Variants,
    IReadOnlyList<QrMenuOptionGroupDto> OptionGroups);

public record QrMenuCategoryDto(
    Guid CategoryId,
    string Name,
    int SortOrder,
    IReadOnlyList<QrMenuProductDto> Products);

public record RevalidatedItemOptionDto(Guid OptionId, string OptionName, decimal ExtraPrice);

public record RevalidatedCartItemDto(
    Guid VariantId,
    Guid ProductId,
    string ProductName,
    string SizeName,
    decimal UnitPrice,
    int Quantity,
    IReadOnlyList<RevalidatedItemOptionDto> Options,
    decimal LineTotal,
    string? Note);

public record ValidatedCartSummary(
    bool IsValid,
    IReadOnlyList<string> Errors,
    IReadOnlyList<RevalidatedCartItemDto> Items,
    decimal Subtotal,
    decimal TotalAmount);

public record SessionBillOrderDto(
    Guid OrderId,
    string OrderCode,
    string Status,
    decimal TotalAmount,
    DateTime CreatedAt,
    IReadOnlyList<RevalidatedCartItemDto> Items);

public record SessionBillSummaryDto(
    Guid SessionId,
    string TableNumber,
    string AreaName,
    string Status,
    DateTime OpenedAt,
    int TotalRounds,
    decimal GrandTotal,
    IReadOnlyList<SessionBillOrderDto> Orders);

public interface IQrOrderingRepository
{
    // SCRUM-66: Quét QR & Phiên bàn
    Task<(CafeTable? Table, TableArea? Area)> GetTableByQrTokenAsync(string qrToken);
    Task<TableSession?> GetActiveSessionByTableIdAsync(Guid tableId);
    Task<TableSession> OpenSessionForTableAsync(Guid branchId, Guid tableId);
    Task<TableSession?> GetSessionByIdAsync(Guid sessionId);

    // SCRUM-70, 71: Menu số cho khách
    Task<IReadOnlyList<QrMenuCategoryDto>> GetMenuByBranchIdAsync(Guid branchId);

    // SCRUM-117: Revalidate giá, tùy chọn, khả dụng
    Task<ValidatedCartSummary> ValidateAndCalculateCartAsync(Guid branchId, IReadOnlyList<CartItemDto> cartItems);

    // SCRUM-116, 68: Tạo đơn hàng theo session
    Task<Order> CreateOrderFromCartAsync(
        Guid branchId,
        Guid sessionId,
        Guid? customerId,
        ValidatedCartSummary validatedCart,
        string? note = null);

    // SCRUM-68: Hóa đơn bàn tổng hợp các đợt gọi món
    Task<SessionBillSummaryDto?> GetSessionBillAsync(Guid sessionId);

    // SCRUM-70: Lấy danh sách đơn hàng của session để theo dõi trạng thái
    Task<IReadOnlyList<SessionBillOrderDto>> GetSessionOrdersAsync(Guid sessionId);

    // SCRUM-69: Đăng ký thành viên khách hàng
    Task<(UserAccount User, LoyaltyAccount Loyalty)> RegisterGuestCustomerAsync(
        Guid tenantId,
        string fullName,
        string phone,
        string? email = null);
}
