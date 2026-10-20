using AICOS.BuildingBlocks.Persistence;
using AICOS.IdentityOrganization.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace AICOS.IdentityOrganization.Api.Persistence;

public sealed class IdentityOrganizationDbContext(DbContextOptions<IdentityOrganizationDbContext> options) : AicosDbContext(options)
{
    protected override string Schema => IdentityOrganizationService.Info.Schema;

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<Branch> Branches => Set<Branch>();

    public DbSet<User> Users => Set<User>();

    public DbSet<UserSession> UserSessions => Set<UserSession>();

    public DbSet<OtpCode> OtpCodes => Set<OtpCode>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public DbSet<SubscriptionPlan> SubscriptionPlans => Set<SubscriptionPlan>();

    public DbSet<TenantSubscription> TenantSubscriptions => Set<TenantSubscription>();

    public DbSet<SubscriptionPayment> SubscriptionPayments => Set<SubscriptionPayment>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnsureAuditLogIsAppendOnly();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnsureAuditLogIsAppendOnly();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>NFR-12: the audit history can only grow. A second line of defence behind the database trigger.</summary>
    private void EnsureAuditLogIsAppendOnly()
    {
        if (ChangeTracker.Entries<AuditLog>().Any(e => e.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException("The audit log is append-only: entries cannot be changed or deleted.");
        }
    }
}
