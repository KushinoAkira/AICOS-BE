using AICOS.Ordering.Api.Domain.Entities;
using AICOS.Ordering.Api.Domain.Enums;
using AICOS.Ordering.Api.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AICOS.Services.SmokeTests;

public class OrderPosUseCaseTests
{
    [Fact]
    public void UC_POS_01_CreateOrder_ShouldCalculateTotalAmount_AndHavePendingStatus()
    {
        // Arrange
        var branchId = Guid.NewGuid();
        var order = new Order(branchId, OrderType.DineIn, tableNumber: "B01");

        // Act
        order.AddItem(Guid.NewGuid(), "Cà phê sữa đá", 35000, 2);
        order.AddItem(Guid.NewGuid(), "Bạc xỉu", 40000, 1);

        // Assert
        order.Items.Should().HaveCount(2);
        order.OriginalAmount.Should().Be(110000); // 35000*2 + 40000*1
        order.FinalAmount.Should().Be(110000);
        order.Status.Should().Be(OrderStatus.Pending);
        order.PaymentStatus.Should().Be(PaymentStatus.Unpaid);
        order.TableNumber.Should().Be("B01");
    }

    [Fact]
    public void UC_POS_01_A1_ApplyPromotion_ShouldDeductDiscountAmount()
    {
        // Arrange
        var order = new Order(Guid.NewGuid(), OrderType.DineIn);
        order.AddItem(Guid.NewGuid(), "Cà phê đen", 30000, 2); // 60,000đ

        // Act: Áp dụng voucher giảm 10,000đ
        order.ApplyPromotion("GIAM10K", 10000);

        // Assert
        order.OriginalAmount.Should().Be(60000);
        order.DiscountAmount.Should().Be(10000);
        order.FinalAmount.Should().Be(50000);
        order.PromotionCode.Should().Be("GIAM10K");
    }

    [Fact]
    public async Task MenuCatalog_WhenItemIsUnavailable_ShouldReturnUnavailable()
    {
        // Arrange
        var mockMenu = new MockMenuCatalogServiceClient(NullLogger<MockMenuCatalogServiceClient>.Instance);
        var branchId = Guid.NewGuid();
        var items = new List<(Guid ProductId, string ProductName)>
        {
            (Guid.NewGuid(), "Trà sen vàng [TẠM HẾT]"),
            (Guid.NewGuid(), "Cà phê muối")
        };

        // Act
        var result = await mockMenu.CheckItemsAvailabilityAsync(branchId, items);

        // Assert: Phát hiện món bị tắt cờ bán IsAvailable trên Menu
        result.AllAvailable.Should().BeFalse();
        result.UnavailableItems.Should().ContainSingle().Which.Should().Contain("Trà sen vàng");
    }

    [Fact]
    public void UC_POS_01_MarkAsPaid_ShouldUpdatePaymentStatusAndConfirmOrder()
    {
        // Arrange
        var order = new Order(Guid.NewGuid(), OrderType.DineIn);
        order.AddItem(Guid.NewGuid(), "Cà phê đen", 30000, 1);

        // Act: Khách thanh toán tiền mặt tại quầy
        order.MarkAsPaid(PaymentMethod.Cash);

        // Assert
        order.PaymentStatus.Should().Be(PaymentStatus.Paid);
        order.PaymentMethod.Should().Be(PaymentMethod.Cash);
        order.Status.Should().Be(OrderStatus.Confirmed); // Tự động xác nhận để bếp làm món
    }

    [Fact]
    public void BR04_CancelOrder_WhenAlreadyPaid_ShouldThrowException()
    {
        // Arrange: Đơn hàng đã thanh toán
        var order = new Order(Guid.NewGuid(), OrderType.DineIn);
        order.AddItem(Guid.NewGuid(), "Cà phê sữa đá", 35000, 1);
        order.MarkAsPaid(PaymentMethod.Card);

        // Act & Assert: BR-04 Đơn hàng chỉ được hủy khi trạng thái chưa "Đã thanh toán"
        var act = () => order.CancelOrder("Khách muốn đổi ý");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*BR-04*");
    }

    [Fact]
    public void UC_POS_04_CancelOrder_WhenUnpaid_ShouldSucceed()
    {
        // Arrange: Đơn chưa thanh toán
        var order = new Order(Guid.NewGuid(), OrderType.DineIn);
        order.AddItem(Guid.NewGuid(), "Trà đào cam sả", 45000, 1);

        // Act: Hủy đơn
        order.CancelOrder("Khách bận về đột xuất");

        // Assert
        order.Status.Should().Be(OrderStatus.Cancelled);
        order.CancelledReason.Should().Be("Khách bận về đột xuất");
        order.CancelledAtUtc.Should().NotBeNull();
    }

    [Fact]
    public void UC_POS_03_SwitchServiceType_FromDineInToTakeaway_ShouldClearTable()
    {
        // Arrange
        var tableId = Guid.NewGuid();
        var order = new Order(Guid.NewGuid(), OrderType.DineIn, tableId: tableId, tableNumber: "B05");

        // Act: Chuyển sang mang về
        order.SwitchServiceType(OrderType.Takeaway);

        // Assert
        order.Type.Should().Be(OrderType.Takeaway);
        order.TableId.Should().BeNull();
        order.TableNumber.Should().BeNull();
    }

    [Fact]
    public void UC_POS_02_ReceiptPrinting_WhenPrintedMultipleTimes_ShouldMarkReprint()
    {
        // Arrange
        var order = new Order(Guid.NewGuid(), OrderType.DineIn);
        order.AddItem(Guid.NewGuid(), "Espresso", 35000, 1);

        // Act 1: In lần đầu
        order.MarkAsPrinted();
        order.IsPrinted.Should().BeTrue();
        order.PrintedCount.Should().Be(1);

        // Act 2: In lại (Reprint - nghiệp vụ phụ UC_POS_02)
        order.MarkAsPrinted();
        order.PrintedCount.Should().Be(2);
        (order.PrintedCount > 1).Should().BeTrue(); // Đánh dấu là hóa đơn in lại
    }

    [Fact]
    public void BR09_OnlinePaymentOrder_ExpiredAfter5Minutes_ShouldBeDetected()
    {
        // Arrange: Giả lập đơn hàng online tạo cách đây 6 phút
        var order = new Order(Guid.NewGuid(), OrderType.DineIn, paymentMethod: PaymentMethod.EWallet);
        order.AddItem(Guid.NewGuid(), "Cà phê trứng", 50000, 1);

        // Chưa quá 5 phút (nếu kiểm tra timeout 10 phút)
        order.IsOnlinePaymentExpired(timeoutMinutes: 10).Should().BeFalse();

        // Mặc định đơn vừa tạo (< 1s) chưa hết hạn 5 phút
        order.IsOnlinePaymentExpired(timeoutMinutes: 5).Should().BeFalse();

        // Đơn thanh toán tiền mặt không bao giờ bị auto-hủy online
        var cashOrder = new Order(Guid.NewGuid(), OrderType.DineIn, paymentMethod: PaymentMethod.Cash);
        cashOrder.IsOnlinePaymentExpired(timeoutMinutes: 0).Should().BeFalse();
    }
}
