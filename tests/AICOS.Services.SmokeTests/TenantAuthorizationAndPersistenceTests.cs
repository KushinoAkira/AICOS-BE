using AICOS.BuildingBlocks.Security;
using AICOS.Ordering.Api.Domain.Entities;
using AICOS.Ordering.Api.Domain.Enums;
using AICOS.Ordering.Api.Persistence;
using AICOS.Ordering.Api.Persistence.Entities;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using Xunit;

namespace AICOS.Services.SmokeTests;

public class TenantAuthorizationAndPersistenceTests
{
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeCurrentUser : ICurrentUser
    {
        public bool IsAuthenticated { get; init; } = true;
        public Guid? UserId { get; init; } = Guid.NewGuid();
        public string? Name { get; init; } = "Test Staff";
        public string? Role { get; init; } = AicosRoles.Cashier;
        public Guid? BranchId { get; set; }
        public Guid? TenantId { get; set; }

        public bool IsInRole(string role) => role == Role;
    }

    [Fact]
    public void JwtTokenService_WithTenantId_ShouldIncludeTenantClaim()
    {
        // Arrange
        var options = Options.Create(new JwtOptions
        {
            SigningKey = "super-secret-key-with-at-least-32-chars-long!",
            Issuer = "aicos-test",
            Audience = "aicos-app",
            ExpiryMinutes = 60
        });
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 22, 10, 0, 0, TimeSpan.Zero));
        var jwtService = new JwtTokenService(options, clock);

        var userId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        // Act
        var (token, expiresAt) = jwtService.CreateAccessToken(
            userId: userId,
            name: "Thu Ngan 1",
            role: AicosRoles.Cashier,
            branchId: branchId,
            tenantId: tenantId
        );

        // Assert
        token.Should().NotBeNullOrWhiteSpace();
        expiresAt.Should().Be(clock.GetUtcNow().UtcDateTime.AddMinutes(60));

        // Giải mã Principal và kiểm tra CurrentUser
        var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(token);

        jwt.Claims.Should().Contain(c => c.Type == AicosClaimTypes.TenantId && c.Value == tenantId.ToString());
        jwt.Claims.Should().Contain(c => c.Type == AicosClaimTypes.BranchId && c.Value == branchId.ToString());
        jwt.Claims.Should().Contain(c => c.Type == AicosClaimTypes.Role && c.Value == AicosRoles.Cashier);
    }

    [Fact]
    public void CurrentUser_ShouldParseTenantId_FromHttpContext()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var claims = new[]
        {
            new Claim(AicosClaimTypes.UserId, userId.ToString()),
            new Claim(AicosClaimTypes.Name, "Nguyen Van A"),
            new Claim(AicosClaimTypes.Role, AicosRoles.Cashier),
            new Claim(AicosClaimTypes.BranchId, branchId.ToString()),
            new Claim(AicosClaimTypes.TenantId, tenantId.ToString())
        };

        var identity = new ClaimsIdentity(claims, "Bearer");
        var principal = new ClaimsPrincipal(identity);
        var httpContext = new DefaultHttpContext { User = principal };
        var accessor = new HttpContextAccessor { HttpContext = httpContext };

        // Act
        var currentUser = new CurrentUser(accessor);

        // Assert
        currentUser.IsAuthenticated.Should().BeTrue();
        currentUser.UserId.Should().Be(userId);
        currentUser.TenantId.Should().Be(tenantId);
        currentUser.BranchId.Should().Be(branchId);
        currentUser.Role.Should().Be(AicosRoles.Cashier);
    }

    [Fact]
    public async Task PostgresOrderRepository_MultiTenancy_ShouldFilterOrdersByTenant()
    {
        // Arrange: DbContext In-Memory
        var dbOptions = new DbContextOptionsBuilder<OrderingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new OrderingDbContext(dbOptions);

        var tenant1 = new TenantEntity { Id = Guid.NewGuid(), Name = "Chuỗi Cafe A", Code = "CAFE_A" };
        var tenant2 = new TenantEntity { Id = Guid.NewGuid(), Name = "Chuỗi Cafe B", Code = "CAFE_B" };
        db.Tenants.AddRange(tenant1, tenant2);

        var branch1 = new BranchEntity { Id = Guid.NewGuid(), TenantId = tenant1.Id, Name = "Chi nhánh Quận 1", Code = "Q1" };
        var branch2 = new BranchEntity { Id = Guid.NewGuid(), TenantId = tenant2.Id, Name = "Chi nhánh Quận 3", Code = "Q3" };
        db.Branches.AddRange(branch1, branch2);

        var order1 = new OrderEntity
        {
            Id = Guid.NewGuid(),
            BranchId = branch1.Id,
            OrderCode = "ORD-TENANT-1",
            TotalAmount = 50000,
            FinalAmount = 50000,
            Status = "SUBMITTED",
            Branch = branch1
        };

        var order2 = new OrderEntity
        {
            Id = Guid.NewGuid(),
            BranchId = branch2.Id,
            OrderCode = "ORD-TENANT-2",
            TotalAmount = 75000,
            FinalAmount = 75000,
            Status = "SUBMITTED",
            Branch = branch2
        };

        db.Orders.AddRange(order1, order2);
        await db.SaveChangesAsync();

        // Caller chỉ thuộc Tenant 1
        var userTenant1 = new FakeCurrentUser { TenantId = tenant1.Id, BranchId = null };
        var repo = new PostgresOrderRepository(
            db,
            new FakeOrderRepository(),
            userTenant1,
            NullLogger<PostgresOrderRepository>.Instance
        );

        // Act
        var orders = (await repo.GetAllAsync()).ToList();

        // Assert: Chỉ thấy đơn hàng của Tenant 1, không thấy của Tenant 2
        orders.Should().ContainSingle();
        orders[0].OrderCode.Should().Be("ORD-TENANT-1");
    }

    [Fact]
    public async Task PostgresOrderRepository_GetById_CrossTenantAccess_ShouldReturnNull()
    {
        // Arrange: DbContext In-Memory
        var dbOptions = new DbContextOptionsBuilder<OrderingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new OrderingDbContext(dbOptions);

        var tenant1 = new TenantEntity { Id = Guid.NewGuid(), Name = "Chuỗi Cafe A", Code = "CAFE_A" };
        var tenant2 = new TenantEntity { Id = Guid.NewGuid(), Name = "Chuỗi Cafe B", Code = "CAFE_B" };
        db.Tenants.AddRange(tenant1, tenant2);

        var branch2 = new BranchEntity { Id = Guid.NewGuid(), TenantId = tenant2.Id, Name = "Chi nhánh B", Code = "CN_B" };
        db.Branches.Add(branch2);

        var orderTenant2 = new OrderEntity
        {
            Id = Guid.NewGuid(),
            BranchId = branch2.Id,
            OrderCode = "ORD-SECRET-B",
            TotalAmount = 100000,
            FinalAmount = 100000,
            Status = "SUBMITTED",
            Branch = branch2
        };
        db.Orders.Add(orderTenant2);
        await db.SaveChangesAsync();

        // Nhân viên thuộc Tenant 1 cố ý truy cập đơn của Tenant 2
        var userTenant1 = new FakeCurrentUser { TenantId = tenant1.Id };
        var repo = new PostgresOrderRepository(
            db,
            new FakeOrderRepository(),
            userTenant1,
            NullLogger<PostgresOrderRepository>.Instance
        );

        // Act & Assert
        var result = await repo.GetByIdAsync(orderTenant2.Id);
        result.Should().BeNull(); // Bị chặn không cho truy cập trái phép chéo Tenant
    }
}
