namespace AICOS.Ordering.Api.Domain.Enums;

public enum OrderStatus
{
    Pending = 1,        // Chờ xử lý (mới tạo)
    Confirmed = 2,      // Đã xác nhận (chuẩn bị làm món)
    Preparing = 3,      // Đang chuẩn bị (nhà bếp/pha chế đang làm)
    Ready = 4,          // Đã làm xong, chờ phục vụ/giao
    Completed = 5,      // Đã hoàn thành (khách đã nhận)
    Cancelled = 6       // Đã hủy
}

public enum OrderType
{
    DineIn = 1,         // Ăn tại bàn
    Takeaway = 2,       // Mang đi
    Delivery = 3        // Giao hàng
}

public enum PaymentStatus
{
    Unpaid = 1,         // Chưa thanh toán
    PartiallyPaid = 2,  // Thanh toán một phần
    Paid = 3,           // Đã thanh toán
    Refunded = 4        // Đã hoàn tiền
}

public enum PaymentMethod
{
    Cash = 1,           // Tiền mặt tại quầy
    Card = 2,           // Thẻ POS (Visa, Master, ATM)
    EWallet = 3,        // Ví điện tử (MoMo, ZaloPay, ShopeePay)
    BankTransfer = 4    // Chuyển khoản VietQR
}
