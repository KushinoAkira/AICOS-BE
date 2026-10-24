using AICOS.BuildingBlocks.Exceptions;
using AICOS.Ordering.Api.Persistence;
using AICOS.Ordering.Api.Persistence.Entities;
using AICOS.Ordering.Api.Services.Takeaway;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AICOS.Services.SmokeTests;

public class TakeawayAndCustomerProfileTests
{
    [Fact]
    public async Task SCRUM_79_CreatePickupOrder_ShouldPriceOnServer_AndHoldSlot()
    {
        await using var db = CreateDb();
        var (tenantId, branchId, customerId, variantId) = await SeedMenuAsync(db);
        var pickup = new PickupOrderService(db);
        var slot = DateTime.UtcNow.Date.AddDays(1).AddHours(15);

        var (created, isNew) = await pickup.CreatePickupOrderAsync(
            customerId,
            tenantId,
            "key-1",
            new CreatePickupOrderRequest(branchId, slot, null, [
                new CreatePickupItemRequest(variantId, 2, [], "ít đá")
            ]),
            CancellationToken.None);

        isNew.Should().BeTrue();
        created.Status.Should().Be(TakeawayOrderStatuses.PendingPayment);
        created.TotalAmount.Should().Be(70000);
        (await db.PickupSlots.SingleAsync()).BookedCount.Should().Be(1);

        var (again, isNewAgain) = await pickup.CreatePickupOrderAsync(
            customerId,
            tenantId,
            "key-1",
            new CreatePickupOrderRequest(branchId, slot, null, [
                new CreatePickupItemRequest(variantId, 2, [], null)
            ]),
            CancellationToken.None);
        isNewAgain.Should().BeFalse();
        again.OrderId.Should().Be(created.OrderId);
    }

    [Fact]
    public async Task SCRUM_80_Webhook_ShouldMarkPaid_AndIssuePickupCode()
    {
        await using var db = CreateDb();
        var (tenantId, branchId, customerId, variantId) = await SeedMenuAsync(db);
        var pickup = new PickupOrderService(db);
        var slot = DateTime.UtcNow.Date.AddDays(1).AddHours(16);
        var (order, _) = await pickup.CreatePickupOrderAsync(
            customerId, tenantId, "pay-1",
            new CreatePickupOrderRequest(branchId, slot, null, [new CreatePickupItemRequest(variantId, 1, [], null)]),
            CancellationToken.None);

        var payment = await pickup.StartPaymentAsync(order.OrderId, customerId, CancellationToken.None);
        await pickup.HandlePaymentWebhookAsync(
            new PaymentWebhookRequest("bank", "evt-1", payment.TransactionRef, payment.Amount, "{}"),
            CancellationToken.None);

        var paid = await db.Orders.SingleAsync(o => o.Id == order.OrderId);
        paid.Status.Should().Be(TakeawayOrderStatuses.Paid);
        paid.PickupCode.Should().NotBeNullOrWhiteSpace();
        (await db.Notifications.CountAsync(n => n.Type == "ORDER_PAID")).Should().Be(1);
    }

    [Fact]
    public async Task SCRUM_118_NearestBranch_ShouldRejectInvalidCoordinates()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new TenantEntity { Id = tenantId, Name = "A", Code = "A" });
        await db.SaveChangesAsync();
        var profiles = new CustomerProfileService(db);

        var act = () => profiles.GetNearestBranchAsync(tenantId, 999, 0, CancellationToken.None);
        await act.Should().ThrowAsync<BusinessRuleException>().Where(e => e.ErrorCode == "INVALID_COORDINATES");
    }

    [Fact]
    public async Task SCRUM_78_PatchMe_ShouldUpdateNameAndPhone_NotEmail()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        db.Tenants.Add(new TenantEntity { Id = tenantId, Name = "A", Code = "A" });
        db.Users.Add(new UserEntity { Id = userId, Email = "keep@aicos.vn", FullName = "Old", Phone = "1" });
        await db.SaveChangesAsync();

        var profiles = new CustomerProfileService(db);
        var me = await profiles.PatchMeAsync(userId, tenantId, new PatchMeRequest("New Name", "0909"), CancellationToken.None);
        me.FullName.Should().Be("New Name");
        me.Phone.Should().Be("0909");
        me.Email.Should().Be("keep@aicos.vn");
    }

    [Fact]
    public async Task SCRUM_121_ExpiryJob_ShouldCancelUnpaidOrders_AndReleaseSlot()
    {
        await using var db = CreateDb();
        var (tenantId, branchId, customerId, variantId) = await SeedMenuAsync(db);
        var pickup = new PickupOrderService(db);
        var slot = DateTime.UtcNow.Date.AddDays(1).AddHours(18);
        var (order, _) = await pickup.CreatePickupOrderAsync(
            customerId, tenantId, "exp-1",
            new CreatePickupOrderRequest(branchId, slot, null, [new CreatePickupItemRequest(variantId, 1, [], null)]),
            CancellationToken.None);

        var entity = await db.Orders.SingleAsync(o => o.Id == order.OrderId);
        entity.PaymentDeadline = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();

        await pickup.RunExpiryJobAsync(CancellationToken.None);

        (await db.Orders.SingleAsync(o => o.Id == order.OrderId)).Status.Should().Be(TakeawayOrderStatuses.Cancelled);
        (await db.PickupSlots.SingleAsync()).BookedCount.Should().Be(0);
    }

    [Fact]
    public async Task SCRUM_82_StaffPickup_ShouldRequireCode_AndAwardPoints()
    {
        await using var db = CreateDb();
        var (tenantId, branchId, customerId, variantId) = await SeedMenuAsync(db);
        var staffId = Guid.NewGuid();
        var tenantUserId = Guid.NewGuid();
        db.Users.Add(new UserEntity { Id = staffId, Email = "staff@aicos.vn", FullName = "Staff" });
        db.TenantUsers.Add(new TenantUserEntity { Id = tenantUserId, TenantId = tenantId, UserId = staffId, Status = "ACTIVE" });
        db.StaffAssignments.Add(new StaffAssignmentEntity { BranchId = branchId, TenantUserId = tenantUserId, Status = "ACTIVE" });
        await db.SaveChangesAsync();

        var pickup = new PickupOrderService(db);
        var slot = DateTime.UtcNow.Date.AddDays(1).AddHours(17);
        var (order, _) = await pickup.CreatePickupOrderAsync(
            customerId, tenantId, "staff-1",
            new CreatePickupOrderRequest(branchId, slot, null, [new CreatePickupItemRequest(variantId, 2, [], null)]),
            CancellationToken.None);
        var payment = await pickup.StartPaymentAsync(order.OrderId, customerId, CancellationToken.None);
        await pickup.HandlePaymentWebhookAsync(
            new PaymentWebhookRequest("bank", "evt-staff", payment.TransactionRef, payment.Amount, null),
            CancellationToken.None);

        await pickup.ChangeStaffStatusAsync(order.OrderId, staffId, TakeawayOrderStatuses.Preparing, null, CancellationToken.None);
        await pickup.ChangeStaffStatusAsync(order.OrderId, staffId, TakeawayOrderStatuses.Ready, null, CancellationToken.None);

        var wrong = () => pickup.ChangeStaffStatusAsync(order.OrderId, staffId, TakeawayOrderStatuses.PickedUp, "0000", CancellationToken.None);
        await wrong.Should().ThrowAsync<BusinessRuleException>().Where(e => e.ErrorCode == "WRONG_PICKUP_CODE");

        var paid = await db.Orders.SingleAsync(o => o.Id == order.OrderId);
        await pickup.ChangeStaffStatusAsync(order.OrderId, staffId, TakeawayOrderStatuses.PickedUp, paid.PickupCode, CancellationToken.None);
        (await db.Orders.SingleAsync(o => o.Id == order.OrderId)).Status.Should().Be(TakeawayOrderStatuses.PickedUp);
        (await db.LoyaltyTransactions.CountAsync(t => t.Type == "EARN")).Should().Be(1);
    }

    [Fact]
    public async Task SCRUM_81_Redeem_ShouldRejectInsufficientPoints()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        db.Tenants.Add(new TenantEntity { Id = tenantId, Name = "A", Code = "A" });
        db.Users.Add(new UserEntity { Id = userId, Email = "c@aicos.vn", FullName = "C" });
        db.LoyaltyAccounts.Add(new LoyaltyAccountEntity { TenantId = tenantId, UserId = userId, TotalPoints = 5, CurrentTier = "STANDARD" });
        var rewardId = Guid.NewGuid();
        db.Rewards.Add(new RewardEntity { Id = rewardId, TenantId = tenantId, Name = "Free drink", RequiredPoints = 20, IsActive = true });
        await db.SaveChangesAsync();

        var profiles = new CustomerProfileService(db);
        var act = () => profiles.RedeemAsync(userId, tenantId, rewardId, CancellationToken.None);
        await act.Should().ThrowAsync<ConflictException>().Where(e => e.ErrorCode == "INSUFFICIENT_POINTS");
    }

    private static OrderingDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<OrderingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new OrderingDbContext(options);
    }

    private static async Task<(Guid TenantId, Guid BranchId, Guid CustomerId, Guid VariantId)> SeedMenuAsync(OrderingDbContext db)
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var variantId = Guid.NewGuid();

        db.Tenants.Add(new TenantEntity { Id = tenantId, Name = "Cafe A", Code = "A" });
        db.Branches.Add(new BranchEntity
        {
            Id = branchId,
            TenantId = tenantId,
            Code = "Q1",
            Name = "Quận 1",
            Status = "ACTIVE",
            Latitude = 10.77,
            Longitude = 106.7
        });
        db.Users.Add(new UserEntity { Id = customerId, Email = "c@aicos.vn", FullName = "Khach" });
        db.Categories.Add(new CategoryEntity { Id = categoryId, TenantId = tenantId, Name = "Coffee" });
        db.Products.Add(new ProductEntity
        {
            Id = productId,
            TenantId = tenantId,
            CategoryId = categoryId,
            Name = "Latte",
            BasePrice = 30000,
            IsActive = true
        });
        db.ProductVariants.Add(new ProductVariantEntity
        {
            Id = variantId,
            ProductId = productId,
            Name = "L",
            PriceAdjustment = 5000,
            IsActive = true
        });
        db.BranchProducts.Add(new BranchProductEntity { BranchId = branchId, ProductId = productId, IsAvailable = true });
        db.BranchPickupSettings.Add(new BranchPickupSettingsEntity
        {
            BranchId = branchId,
            OpenTime = new TimeOnly(7, 0),
            CloseTime = new TimeOnly(22, 0),
            SlotMinutes = 15,
            CapacityPerSlot = 5,
            CutoffMinutes = 0,
            HoldMinutes = 15
        });
        await db.SaveChangesAsync();
        return (tenantId, branchId, customerId, variantId);
    }
}
