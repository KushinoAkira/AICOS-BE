using AICOS.BuildingBlocks.Persistence;
using AICOS.IdentityOrganization.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace AICOS.IdentityOrganization.Api.Persistence;

public sealed class IdentityOrganizationDbContext(DbContextOptions<IdentityOrganizationDbContext> options) : AicosDbContext(options)
{
    protected override string Schema => IdentityOrganizationService.Info.Schema;

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<UserAccount> Users => Set<UserAccount>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<TenantUser> TenantUsers => Set<TenantUser>();
    public DbSet<StaffAssignment> StaffAssignments => Set<StaffAssignment>();
    public DbSet<RefreshSession> RefreshSessions => Set<RefreshSession>();
}
