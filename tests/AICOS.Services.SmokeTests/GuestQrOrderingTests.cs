using AICOS.Ordering.Api.Domain.Interfaces;
using AICOS.Ordering.Api.Persistence;
using AICOS.Ordering.Api.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AICOS.Services.SmokeTests;

public class GuestQrOrderingTests
{
    private readonly IQrOrderingRepository _qrRepo;
    private readonly IRedisCartService _cartService;

    public GuestQrOrderingTests()
    {
        _qrRepo = new FakeQrOrderingRepository();
        var config = new ConfigurationBuilder().Build();
        _cartService = new RedisCartService(config, NullLogger<RedisCartService>.Instance);
    }

    [Fact]
    public async Task SCRUM_66_ScanQr_ValidToken_ShouldOpenOrReturnTableSession()
    {
        // Act: Quét QR của Bàn 01 (token: QR-TABLE-01)
        var (table, area) = await _qrRepo.GetTableByQrTokenAsync("QR-TABLE-01");

        // Assert
        table.Should().NotBeNull();
        table!.TableNumber.Should().Be("Bàn 01");
        area.Should().NotBeNull();
        area!.Name.Should().Contain("Phòng Máy Lạnh");

        // Mở phiên bàn
        var session = await _qrRepo.OpenSessionForTableAsync(area.BranchId, table.Id);
        session.Should().NotBeNull();
        session.Status.Should().Be("OPEN");

        // Quét lại cùng bàn thì trả về cùng 1 phiên bàn đang mở
        var recheckSession = await _qrRepo.GetActiveSessionByTableIdAsync(table.Id);
        recheckSession!.Id.Should().Be(session.Id);
    }

    [Fact]
    public async Task SCRUM_70_71_GetMenu_ShouldReturnStructuredCategories_AndAvailabilityFlags()
    {
        // Act: Lấy menu số
        var categories = await _qrRepo.GetMenuByBranchIdAsync(FakeQrOrderingRepository.DefaultBranchId);

        // Assert
        categories.Should().NotBeEmpty();
        var coffeeCat = categories.FirstOrDefault(c => c.Name.Contains("Cà Phê"));
        coffeeCat.Should().NotBeNull();

        // Cà phê sữa đá còn hàng
        var suaDa = coffeeCat!.Products.FirstOrDefault(p => p.Name.Contains("Cà Phê Sữa Đá"));
        suaDa.Should().NotBeNull();
        suaDa!.IsAvailable.Should().BeTrue();
        suaDa.Variants.Should().HaveCount(2); // Size M, L

        // Cà phê muối hết hàng (is_available = false)
        var muoi = coffeeCat.Products.FirstOrDefault(p => p.Name.Contains("Cà Phê Muối"));
        muoi.Should().NotBeNull();
        muoi!.IsAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task SCRUM_67_RedisCart_Add_Update_Clear_ShouldManageSessionCart()
    {
        // Arrange
        var sessionId = Guid.NewGuid();

        // Act 1: Thêm món vào giỏ
        var addRequest = new AddCartItemRequest(
            VariantId: Guid.NewGuid(),
            ProductId: Guid.NewGuid(),
            ProductName: "Trà Sữa Ô Long",
            SizeName: "Size L",
            UnitPrice: 45000,
            Quantity: 2,
            Options: new List<CartItemOptionDto> { new(Guid.NewGuid(), "Trân châu đen", 8000) },
            Note: "Ít đá"
        );
        var cart = await _cartService.AddItemAsync(sessionId, addRequest);

        // Assert: 2 ly * (45k + 8k) = 106k
        cart.Items.Should().HaveCount(1);
        cart.TotalItems.Should().Be(2);
        cart.EstimatedTotal.Should().Be(106000);

        // Act 2: Cập nhật số lượng lên 3 ly
        var itemId = cart.Items[0].ItemId;
        var updated = await _cartService.UpdateItemQuantityAsync(sessionId, itemId, 3);
        updated.TotalItems.Should().Be(3);
        updated.EstimatedTotal.Should().Be(159000);

        // Act 3: Xóa giỏ
        await _cartService.ClearCartAsync(sessionId);
        var cleared = await _cartService.GetCartAsync(sessionId);
        cleared.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task SCRUM_117_RevalidateCart_WhenItemOutOfStock_ShouldReturnInvalid()
    {
        // Arrange: Giỏ hàng chứa món Cà phê muối (vốn đã hết hàng ở branch_products)
        var menu = await _qrRepo.GetMenuByBranchIdAsync(FakeQrOrderingRepository.DefaultBranchId);
        var muoiProd = menu.SelectMany(c => c.Products).First(p => p.Name.Contains("Muối"));
        var muoiVariant = muoiProd.Variants.First();

        var cartItems = new List<CartItemDto>
        {
            new(Guid.NewGuid(), muoiVariant.VariantId, muoiProd.ProductId, muoiProd.Name, muoiVariant.SizeName, muoiVariant.Price, 1, Array.Empty<CartItemOptionDto>(), null)
        };

        // Act: Revalidate trên Server
        var validation = await _qrRepo.ValidateAndCalculateCartAsync(FakeQrOrderingRepository.DefaultBranchId, cartItems);

        // Assert: Bắt buộc phải bị từ chối
        validation.IsValid.Should().BeFalse();
        validation.Errors.Should().Contain(e => e.Contains("TẠM HẾT HÀNG"));
    }

    [Fact]
    public async Task SCRUM_117_RevalidateCart_ValidItems_ShouldCalculateAccurateServerPrices()
    {
        // Arrange: Món Cà phê sữa đá Size M (32k)
        var menu = await _qrRepo.GetMenuByBranchIdAsync(FakeQrOrderingRepository.DefaultBranchId);
        var suaDaProd = menu.SelectMany(c => c.Products).First(p => p.Name.Contains("Sữa Đá"));
        var suaDaVariant = suaDaProd.Variants.First(v => v.SizeName == "Size M");

        // Client cố tình gửi giá sai lệch 1,000đ
        var clientCart = new List<CartItemDto>
        {
            new(Guid.NewGuid(), suaDaVariant.VariantId, suaDaProd.ProductId, suaDaProd.Name, suaDaVariant.SizeName, 1000, 2, Array.Empty<CartItemOptionDto>(), "Nhiều đá")
        };

        // Act: Server revalidate
        var validation = await _qrRepo.ValidateAndCalculateCartAsync(FakeQrOrderingRepository.DefaultBranchId, clientCart);

        // Assert: Server tính lại đúng theo DB: 32k * 2 = 64k
        validation.IsValid.Should().BeTrue();
        validation.TotalAmount.Should().Be(64000);
        validation.Items[0].UnitPrice.Should().Be(32000);
    }

    [Fact]
    public async Task SCRUM_116_68_Checkout_MultipleRounds_ShouldAggregateToSessionBill()
    {
        // 1. Quét QR mở bàn
        var (table, area) = await _qrRepo.GetTableByQrTokenAsync("QR-TABLE-01");
        var session = await _qrRepo.OpenSessionForTableAsync(area!.BranchId, table!.Id);

        // Lấy thông tin món hợp lệ từ menu
        var menu = await _qrRepo.GetMenuByBranchIdAsync(area.BranchId);
        var suaDa = menu.SelectMany(c => c.Products).First(p => p.Name.Contains("Sữa Đá"));
        var suaDaM = suaDa.Variants.First(v => v.SizeName == "Size M");

        var traSua = menu.SelectMany(c => c.Products).First(p => p.Name.Contains("Trà Sữa"));
        var traSuaL = traSua.Variants.First(v => v.SizeName == "Size L");
        var toppingPudding = traSua.OptionGroups.First().Options.First(o => o.Name.Contains("Pudding"));

        // 2. Gọi món đợt 1 (Cà phê sữa đá 32k * 1)
        var cartRound1 = new List<CartItemDto>
        {
            new(Guid.NewGuid(), suaDaM.VariantId, suaDa.ProductId, suaDa.Name, suaDaM.SizeName, suaDaM.Price, 1, Array.Empty<CartItemOptionDto>(), null)
        };
        var val1 = await _qrRepo.ValidateAndCalculateCartAsync(area.BranchId, cartRound1);
        var order1 = await _qrRepo.CreateOrderFromCartAsync(area.BranchId, session.Id, null, val1);
        order1.TotalAmount.Should().Be(32000);

        // 3. Gọi thêm món đợt 2 (Trà sữa size L 45k + Pudding 10k = 55k)
        var cartRound2 = new List<CartItemDto>
        {
            new(Guid.NewGuid(), traSuaL.VariantId, traSua.ProductId, traSua.Name, traSuaL.SizeName, traSuaL.Price, 1,
                new[] { new CartItemOptionDto(toppingPudding.OptionId, toppingPudding.Name, toppingPudding.ExtraPrice) }, null)
        };
        var val2 = await _qrRepo.ValidateAndCalculateCartAsync(area.BranchId, cartRound2);
        var order2 = await _qrRepo.CreateOrderFromCartAsync(area.BranchId, session.Id, null, val2);
        order2.TotalAmount.Should().Be(55000);

        // 4. Lấy hóa đơn tổng hợp của bàn (Session Bill)
        var bill = await _qrRepo.GetSessionBillAsync(session.Id);
        bill.Should().NotBeNull();
        bill!.TotalRounds.Should().Be(2); // 2 đợt gọi món
        bill.GrandTotal.Should().Be(32000 + 55000); // 87,000đ
        bill.TableNumber.Should().Be("Bàn 01");
    }

    [Fact]
    public async Task SCRUM_69_GuestSignUp_ShouldCreateUserAndLoyaltyAccount()
    {
        // Act: Khách đăng ký thành viên
        var (user, loyalty) = await _qrRepo.RegisterGuestCustomerAsync(
            tenantId: FakeQrOrderingRepository.DefaultTenantId,
            fullName: "Nguyễn Văn Khách",
            phone: "0909123456",
            email: "khach@gmail.com"
        );

        // Assert
        user.Should().NotBeNull();
        user.FullName.Should().Be("Nguyễn Văn Khách");
        user.Phone.Should().Be("0909123456");

        loyalty.Should().NotBeNull();
        loyalty.UserId.Should().Be(user.Id);
        loyalty.PointsBalance.Should().Be(10); // Tặng 10 điểm chào mừng
        loyalty.Tier.Should().Be("STANDARD");
    }
}
