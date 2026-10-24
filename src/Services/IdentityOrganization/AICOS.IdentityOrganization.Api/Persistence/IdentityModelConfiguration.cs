using AICOS.IdentityOrganization.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AICOS.IdentityOrganization.Api.Persistence;

internal sealed class UserAccountConfiguration : IEntityTypeConfiguration<UserAccount>
{
    public void Configure(EntityTypeBuilder<UserAccount> builder)
    {
        builder.ToTable("users");
        builder.HasKey(user => user.Id);
        builder.Property(user => user.Email)
            .HasMaxLength(255)
            .HasConversion(email => email.Trim().ToLowerInvariant(), email => email)
            .IsRequired();
        builder.HasIndex(user => user.Email).IsUnique();
        builder.Property(user => user.PasswordHash).HasColumnType("text");
        builder.Property(user => user.FullName).HasMaxLength(200).IsRequired();
        builder.Property(user => user.Phone).HasMaxLength(20);
        builder.Property(user => user.Status).HasMaxLength(30).HasDefaultValue(IdentityStatuses.Active);
        builder.Property(user => user.CreatedAt)
            .HasColumnType("timestamp without time zone")
            .HasDefaultValueSql("now()");
    }
}
internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("tenants");
        builder.HasKey(tenant => tenant.Id);
        builder.Property(tenant => tenant.Name).HasMaxLength(200).IsRequired();
        builder.Property(tenant => tenant.Code).HasMaxLength(50).IsRequired();
        builder.HasIndex(tenant => tenant.Code).IsUnique();
        builder.Property(tenant => tenant.Status).HasMaxLength(30).HasDefaultValue(IdentityStatuses.Active).IsRequired();
        builder.Property(tenant => tenant.CreatedAt)
            .HasColumnType("timestamp without time zone")
            .HasDefaultValueSql("now()");
        builder.Property(tenant => tenant.UpdatedAt).HasColumnType("timestamp without time zone");
    }
}

internal sealed class BranchConfiguration : IEntityTypeConfiguration<Branch>
{
    public void Configure(EntityTypeBuilder<Branch> builder)
    {
        builder.ToTable("branches");
        builder.HasKey(branch => branch.Id);
        builder.Property(branch => branch.Code).HasMaxLength(50).IsRequired();
        builder.Property(branch => branch.Name).HasMaxLength(200).IsRequired();
        builder.Property(branch => branch.Phone).HasMaxLength(20);
        builder.Property(branch => branch.Status).HasMaxLength(30).HasDefaultValue(IdentityStatuses.Inactive);
        builder.Property(branch => branch.CreatedAt)
            .HasColumnType("timestamp without time zone")
            .HasDefaultValueSql("now()");
        builder.HasIndex(branch => new { branch.TenantId, branch.Code }).IsUnique();
        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(branch => branch.TenantId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.ToTable("roles");
        builder.HasKey(role => role.Id);
        builder.Property(role => role.Code).HasMaxLength(50).IsRequired();
        builder.Property(role => role.Name).HasMaxLength(100).IsRequired();
        builder.HasIndex(role => role.Code).IsUnique();
    }
}

internal sealed class TenantUserConfiguration : IEntityTypeConfiguration<TenantUser>
{
    public void Configure(EntityTypeBuilder<TenantUser> builder)
    {
        builder.ToTable("tenant_users");
        builder.HasKey(membership => membership.Id);
        builder.Property(membership => membership.Status).HasMaxLength(30).HasDefaultValue(IdentityStatuses.Active);
        builder.HasIndex(membership => new { membership.TenantId, membership.UserId, membership.RoleId }).IsUnique();
        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(membership => membership.TenantId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasOne(membership => membership.User)
            .WithMany()
            .HasForeignKey(membership => membership.UserId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasOne(membership => membership.Role)
            .WithMany()
            .HasForeignKey(membership => membership.RoleId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class StaffAssignmentConfiguration : IEntityTypeConfiguration<StaffAssignment>
{
    public void Configure(EntityTypeBuilder<StaffAssignment> builder)
    {
        builder.ToTable("staff_assignments");
        builder.HasKey(assignment => assignment.Id);
        builder.Property(assignment => assignment.Status).HasMaxLength(30).HasDefaultValue(IdentityStatuses.Active);
        builder.HasIndex(assignment => new { assignment.BranchId, assignment.TenantUserId }).IsUnique();
        builder.HasOne(assignment => assignment.Branch)
            .WithMany()
            .HasForeignKey(assignment => assignment.BranchId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasOne(assignment => assignment.TenantUser)
            .WithMany()
            .HasForeignKey(assignment => assignment.TenantUserId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
