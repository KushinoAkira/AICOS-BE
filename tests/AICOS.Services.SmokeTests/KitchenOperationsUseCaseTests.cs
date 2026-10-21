using AICOS.KitchenOperations.Api.Domain.Entities;
using AICOS.KitchenOperations.Api.Domain.Enums;
using AICOS.KitchenOperations.Api.Persistence;
using FluentAssertions;
using Xunit;

namespace AICOS.Services.SmokeTests;

public class KitchenOperationsUseCaseTests
{
    [Fact]
    public void UC_KITCHEN_01_StatusProgression_AtOrderLevel_ShouldSucceed()
    {
        // Arrange
        var branchId = Guid.NewGuid();
        var ticket = new KitchenTicket(
            orderId: Guid.NewGuid(),
            orderCode: "ORD-001",
            branchId: branchId,
            tableNumber: "T01",
            orderType: "DineIn",
            isPaid: false
        );
        ticket.AddItem(Guid.NewGuid(), "Cà phê sữa đá", 2, "Ít ngọt");

        // Assert initial
        ticket.Status.Should().Be(KitchenTicketStatus.Pending);

        // Act 1: Start Brewing
        ticket.StartBrewing();
        ticket.Status.Should().Be(KitchenTicketStatus.Brewing);
        ticket.BrewingStartedAtUtc.Should().NotBeNull();

        // Act 2: Mark Ready for Pickup (Order level)
        ticket.MarkReadyForPickup();
        ticket.Status.Should().Be(KitchenTicketStatus.ReadyForPickup);
        ticket.ReadyAtUtc.Should().NotBeNull();

        // Act 3: Completed
        ticket.Complete();
        ticket.Status.Should().Be(KitchenTicketStatus.Completed);
        ticket.CompletedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task BR41_QueueSorting_PriorityAppointments_ShouldComeBeforeRegularFIFO()
    {
        // Arrange
        var repo = new FakeKitchenTicketRepository();
        var branchId = Guid.NewGuid();

        // Đơn 1: Đơn thường tạo lúc 10:00 (FIFO đầu tiên)
        var regular1 = new KitchenTicket(Guid.NewGuid(), "ORD-REG-1", branchId, "T01", "DineIn", true);
        regular1.AddItem(Guid.NewGuid(), "Espresso", 1);
        await repo.AddAsync(regular1);

        // Đơn 2: Đơn thường tạo sau (10:05)
        var regular2 = new KitchenTicket(Guid.NewGuid(), "ORD-REG-2", branchId, "T02", "DineIn", true);
        regular2.AddItem(Guid.NewGuid(), "Americano", 1);
        await repo.AddAsync(regular2);

        // Đơn 3: Đơn Takeaway có hẹn giờ lấy lúc 10:15 (Ưu tiên BR-41)
        var priorityTicket = new KitchenTicket(
            Guid.NewGuid(), "ORD-PRIORITY", branchId, null, "Takeaway", true,
            scheduledPickupTimeUtc: DateTime.UtcNow.AddMinutes(15)
        );
        priorityTicket.AddItem(Guid.NewGuid(), "Trà sữa Ô Long", 1);
        await repo.AddAsync(priorityTicket);

        // Act: Lấy hàng đợi
        var queue = await repo.GetQueueAsync(branchId);

        // Assert: Đơn có giờ hẹn ưu tiên phải đứng ĐẦU HÀNG ĐỢI (BR-41)
        queue.Should().HaveCount(3);
        queue[0].OrderCode.Should().Be("ORD-PRIORITY"); // Ưu tiên giờ hẹn
        queue[1].OrderCode.Should().Be("ORD-REG-1");    // FIFO đơn thường 1
        queue[2].OrderCode.Should().Be("ORD-REG-2");    // FIFO đơn thường 2
    }

    [Fact]
    public async Task BR42_BatchAggregation_ShouldMergeSameItems_UpToMax4CupsPerBatch()
    {
        // Arrange: 2 đơn khác nhau cùng gọi "Trà sữa Ô Long" trong khung 5 phút
        var repo = new FakeKitchenTicketRepository();
        var branchId = Guid.NewGuid();

        // Đơn 1: 3 ly Trà sữa Ô Long
        var ticket1 = new KitchenTicket(Guid.NewGuid(), "ORD-001", branchId, "B01", "DineIn", true);
        ticket1.AddItem(Guid.NewGuid(), "Trà sữa Ô Long", 3, "50% đường, không đá");
        await repo.AddAsync(ticket1);

        // Đơn 2: 2 ly Trà sữa Ô Long + 1 ly Cà phê đen
        var ticket2 = new KitchenTicket(Guid.NewGuid(), "ORD-002", branchId, "B02", "DineIn", true);
        ticket2.AddItem(Guid.NewGuid(), "Trà sữa Ô Long", 2, "100% đường, trân châu");
        ticket2.AddItem(Guid.NewGuid(), "Cà phê đen", 1);
        await repo.AddAsync(ticket2);

        // Act: Lấy các mẻ pha chế gộp tự động
        var batches = await repo.GetActiveBatchesAsync(branchId, TimeSpan.FromMinutes(5), maxBatchSize: 4);

        // Assert
        // Tổng số ly Trà sữa Ô Long = 3 + 2 = 5 ly -> phải tách thành 2 mẻ (1 mẻ 4 ly, 1 mẻ 1 ly)
        var oolongBatches = batches.Where(b => b.ProductName == "Trà sữa Ô Long").ToList();
        oolongBatches.Should().HaveCount(2);
        oolongBatches[0].TotalQuantity.Should().Be(4); // Mẻ 1: đầy 4 ly
        oolongBatches[1].TotalQuantity.Should().Be(1); // Mẻ 2: 1 ly dư

        // Món Cà phê đen: 1 mẻ 1 ly
        var coffeeBatches = batches.Where(b => b.ProductName == "Cà phê đen").ToList();
        coffeeBatches.Should().HaveCount(1);
        coffeeBatches[0].TotalQuantity.Should().Be(1);
    }

    [Fact]
    public void UC_KITCHEN_02_AutoPrintStickers_BR43_DineIn_ShouldGenerateStickersImmediately()
    {
        // Arrange: Đơn Dine-in chưa thanh toán (tạo tại bàn)
        var ticket = new KitchenTicket(Guid.NewGuid(), "ORD-DINEIN", Guid.NewGuid(), "B05", "DineIn", isPaid: false);
        ticket.AddItem(Guid.NewGuid(), "Trà sen vàng", 2, "Ít ngọt, thêm hạt sen");

        // Act: Thử sinh tem ly nước
        bool printed = ticket.TryGenerateCupStickers(out var stickers);

        // Assert: Dine-in được in tem ngay khi xác nhận tạo đơn (BR-43)
        printed.Should().BeTrue();
        stickers.Should().HaveCount(2);

        // Kiểm tra thông tin nhãn từng ly
        stickers[0].CupIndex.Should().Be(1);
        stickers[0].TotalCupsInOrder.Should().Be(2);
        stickers[0].ProductName.Should().Be("Trà sen vàng");
        stickers[0].Customizations.Should().Be("Ít ngọt, thêm hạt sen");
        stickers[0].TableNumber.Should().Be("B05");

        stickers[1].CupIndex.Should().Be(2);
        stickers[1].TotalCupsInOrder.Should().Be(2);
    }

    [Fact]
    public void UC_KITCHEN_02_AutoPrintStickers_BR43_OnlineUnpaid_ShouldNotGenerateUntilPaid()
    {
        // Arrange: Đơn Online giao hàng nhưng CHƯA thanh toán
        var ticket = new KitchenTicket(Guid.NewGuid(), "ORD-ONLINE", Guid.NewGuid(), null, "Delivery", isPaid: false);
        ticket.AddItem(Guid.NewGuid(), "Bạc xỉu", 1);

        // Act 1: Thử sinh tem khi chưa thanh toán
        bool printedBeforePaid = ticket.TryGenerateCupStickers(out var stickersBefore);
        printedBeforePaid.Should().BeFalse();
        stickersBefore.Should().BeEmpty();

        // Act 2: Khách hoàn tất thanh toán online
        ticket.MarkAsPaid();
        bool printedAfterPaid = ticket.TryGenerateCupStickers(out var stickersAfter);

        // Assert: Sau khi thanh toán mới được in tem (BR-43)
        printedAfterPaid.Should().BeTrue();
        stickersAfter.Should().HaveCount(1);
        stickersAfter[0].ProductName.Should().Be("Bạc xỉu");
    }
}
