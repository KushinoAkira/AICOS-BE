using AICOS.IdentityOrganization.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AICOS.IdentityOrganization.Api.Persistence.Configurations;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");
        builder.HasKey(u => u.Id);
        builder.Property(u => u.Id).ValueGeneratedNever();

        builder.Property(u => u.FullName).HasMaxLength(User.FullNameMaxLength).IsRequired();
        builder.Property(u => u.Email).HasMaxLength(User.EmailMaxLength).IsRequired();
        builder.Property(u => u.Username).HasMaxLength(User.UsernameMaxLength);
        builder.Property(u => u.Phone).HasMaxLength(User.PhoneMaxLength);
        builder.Property(u => u.PasswordHash).HasMaxLength(255).IsRequired();
        builder.Property(u => u.Role).HasMaxLength(20).IsRequired();
        builder.Property(u => u.Status).HasConversion<string>().HasMaxLength(20).IsRequired();

        // Unique across deleted users too: an email/username is never reused (audit trail stays unambiguous).
        builder.HasIndex(u => u.Email).IsUnique();
        builder.HasIndex(u => u.Username).IsUnique();
        builder.HasIndex(u => u.Role);
        builder.HasIndex(u => u.BranchId);
        builder.HasIndex(u => u.TenantId);
        builder.HasOne<Tenant>().WithMany().HasForeignKey(u => u.TenantId).OnDelete(DeleteBehavior.Restrict);

        // branches lives in the same service/schema, so a real FK is allowed here.
        builder.HasOne<Branch>().WithMany().HasForeignKey(u => u.BranchId).OnDelete(DeleteBehavior.Restrict);

        builder.HasQueryFilter(u => u.DeletedAtUtc == null);
    }
}

internal sealed class UserSessionConfiguration : IEntityTypeConfiguration<UserSession>
{
    public void Configure(EntityTypeBuilder<UserSession> builder)
    {
        builder.ToTable("user_sessions");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();

        builder.Property(s => s.RefreshTokenHash).HasMaxLength(64).IsRequired();
        builder.Property(s => s.DeviceLabel).HasMaxLength(UserSession.DeviceLabelMaxLength);
        builder.Property(s => s.IpAddress).HasMaxLength(UserSession.IpAddressMaxLength);

        builder.HasIndex(s => s.RefreshTokenHash).IsUnique();
        builder.HasIndex(s => s.UserId);

        builder.HasOne(s => s.User).WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasQueryFilter(s => s.User!.DeletedAtUtc == null);
    }
}

internal sealed class OtpCodeConfiguration : IEntityTypeConfiguration<OtpCode>
{
    public void Configure(EntityTypeBuilder<OtpCode> builder)
    {
        builder.ToTable("otp_codes");
        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).ValueGeneratedNever();

        builder.Property(o => o.Target).HasMaxLength(User.EmailMaxLength).IsRequired();
        builder.Property(o => o.CodeHash).HasMaxLength(64).IsRequired();
        builder.Property(o => o.Purpose).HasMaxLength(30).IsRequired();

        builder.HasIndex(o => new { o.Target, o.Purpose, o.CreatedAtUtc });
    }
}
