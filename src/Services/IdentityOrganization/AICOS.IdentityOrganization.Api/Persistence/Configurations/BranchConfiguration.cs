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

        builder.HasIndex(b => new { b.TenantId, b.Name }).IsUnique();
        builder.HasOne<Tenant>().WithMany().HasForeignKey(b => b.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(b => b.Status);
    }
}

internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("tenants");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();

        builder.Property(t => t.Name).HasMaxLength(Tenant.NameMaxLength).IsRequired();
        builder.Property(t => t.Code).HasMaxLength(Tenant.CodeMaxLength).IsRequired();
        builder.Property(t => t.Status).HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.Property(t => t.Description).HasMaxLength(Tenant.DescriptionMaxLength);
        builder.Property(t => t.LogoUrl).HasMaxLength(Tenant.LogoUrlMaxLength);
        builder.Property(t => t.PrimaryColor).HasMaxLength(7);
        builder.Property(t => t.SecondaryColor).HasMaxLength(7);
        builder.Property(t => t.ReviewNote).HasMaxLength(Tenant.ReviewNoteMaxLength);

        builder.HasIndex(t => t.Code).IsUnique();
        builder.HasIndex(t => t.Status);
    }
}
