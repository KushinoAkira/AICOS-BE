using AICOS.IdentityOrganization.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AICOS.IdentityOrganization.Api.Persistence.Configurations;

internal sealed class BranchConfiguration : IEntityTypeConfiguration<Branch>
{
    public void Configure(EntityTypeBuilder<Branch> builder)
    {
        builder.ToTable("branches");
        builder.HasKey(b => b.Id);
        builder.Property(b => b.Id).ValueGeneratedNever();

        builder.Property(b => b.Name).HasMaxLength(Branch.NameMaxLength).IsRequired();
        builder.Property(b => b.Address).HasMaxLength(Branch.AddressMaxLength);
        builder.Property(b => b.Phone).HasMaxLength(Branch.PhoneMaxLength);
        builder.Property(b => b.Status).HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.HasIndex(b => b.Name).IsUnique();
        builder.HasIndex(b => b.Status);
    }
}
