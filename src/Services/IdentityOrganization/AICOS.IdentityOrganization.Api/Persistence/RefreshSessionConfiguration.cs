using AICOS.IdentityOrganization.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AICOS.IdentityOrganization.Api.Persistence;

internal sealed class RefreshSessionConfiguration : IEntityTypeConfiguration<RefreshSession>
{
    public void Configure(EntityTypeBuilder<RefreshSession> builder)
    {
        builder.ToTable("refresh_sessions");
        builder.HasKey(session => session.Id);
        builder.Property(session => session.TokenHash).HasMaxLength(64).IsFixedLength().IsRequired();
        builder.HasIndex(session => session.TokenHash).IsUnique();
        builder.HasIndex(session => new { session.UserId, session.BranchId, session.ExpiresAtUtc });
        builder.Property(session => session.Version).IsConcurrencyToken();
        builder.HasOne<UserAccount>()
            .WithMany()
            .HasForeignKey(session => session.UserId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<Branch>()
            .WithMany()
            .HasForeignKey(session => session.BranchId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<RefreshSession>()
            .WithMany()
            .HasForeignKey(session => session.ReplacedBySessionId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
