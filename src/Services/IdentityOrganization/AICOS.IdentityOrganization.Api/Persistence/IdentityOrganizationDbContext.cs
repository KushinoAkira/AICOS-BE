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
}