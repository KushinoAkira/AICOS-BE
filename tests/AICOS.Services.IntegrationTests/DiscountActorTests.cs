using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Ordering.Api.Features.SessionBilling;

namespace AICOS.Services.IntegrationTests;

public sealed class DiscountActorTests
{
    private sealed class Actor : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid? UserId { get; } = Guid.NewGuid();
        public Guid? TenantId { get; } = Guid.NewGuid();
        public Guid? BranchId { get; } = Guid.NewGuid();
        public string Name => "Test";
        public string Role => "Server";
        public bool IsInRole(string role) => role is "Server" or "Cashier";
    }

    [Theory]
    [InlineData("valid", true)]
    [InlineData("revoked", false)]
    [InlineData("branch", false)]
    [InlineData("tenant", false)]
    [InlineData("password", false)]
    [InlineData("inactive", false)]
    [InlineData("locked", false)]
    public void Current_identity_and_secondary_cashier_role_are_required(string scenario, bool expected)
    {
        var actor = new Actor(); var now = DateTimeOffset.UtcNow;
        var me = JsonSerializer.SerializeToElement(new
        {
            id = actor.UserId, tenantId = scenario == "tenant" ? Guid.NewGuid() : actor.TenantId,
            branchId = scenario == "branch" ? Guid.NewGuid() : actor.BranchId,
            role = "Server", roles = scenario == "revoked" ? new[] { "Server" } : new[] { "Server", "Cashier" },
            status = scenario == "inactive" ? "Inactive" : "Active", mustChangePassword = scenario == "password",
            lockoutEndUtc = scenario == "locked" ? now.AddMinutes(10).ToString("O") : null
        });
        DiscountActorVerifier.Valid(me, actor, now).Should().Be(expected);
    }
}
