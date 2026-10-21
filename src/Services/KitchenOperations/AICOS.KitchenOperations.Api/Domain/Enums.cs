namespace AICOS.KitchenOperations.Api.Domain.Enums;

public enum KitchenTicketStatus
{
    Pending = 1,        // Chờ pha chế (mới vào hàng đợi)
    Brewing = 2,        // Đang pha chế (Barista đang thao tác)
    ReadyForPickup = 3, // Pha chế xong, sẵn sàng phục vụ khách
    Completed = 4,      // Khách đã nhận món / hoàn tất
    Cancelled = 5       // Đã hủy
}
