using AICOS.Payments.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AICOS.Payments.Api.Persistence;

internal sealed class BillConfiguration : IEntityTypeConfiguration<Bill>
{
    public void Configure(EntityTypeBuilder<Bill> builder)
    {
        builder.ToTable("bills");
        builder.HasKey(bill => bill.Id);
        builder.Property(bill => bill.BillCode).HasMaxLength(50).IsRequired();
        builder.HasIndex(bill => bill.BillCode).IsUnique();
        builder.Property(bill => bill.Status).HasMaxLength(30).HasDefaultValue(BillStatuses.Unpaid);
        builder.Property(bill => bill.TotalAmount).HasPrecision(18, 2).IsRequired();
        builder.Property(bill => bill.CreatedAt)
            .HasColumnType("timestamp without time zone")
            .HasDefaultValueSql("now()");
        builder.HasMany(bill => bill.Items)
            .WithOne(item => item.Bill)
            .HasForeignKey(item => item.BillId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasMany(bill => bill.Payments)
            .WithOne(payment => payment.Bill)
            .HasForeignKey(payment => payment.BillId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class BillItemConfiguration : IEntityTypeConfiguration<BillItem>
{
    public void Configure(EntityTypeBuilder<BillItem> builder)
    {
        builder.ToTable("bill_items");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Amount).HasPrecision(18, 2).IsRequired();
    }
}

internal sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        builder.ToTable("payments");
        builder.HasKey(payment => payment.Id);
        builder.Property(payment => payment.Method).HasMaxLength(30).IsRequired();
        builder.Property(payment => payment.Status)
            .HasMaxLength(30)
            .HasDefaultValue(PaymentStatuses.Pending);
        builder.Property(payment => payment.Amount).HasPrecision(18, 2).IsRequired();
        builder.Property(payment => payment.TransactionReference)
            .HasColumnName("transaction_ref")
            .HasMaxLength(255);
        builder.HasIndex(payment => payment.TransactionReference).IsUnique();
        builder.Property(payment => payment.PaidAt).HasColumnType("timestamp without time zone");
        builder.Property(payment => payment.CreatedAt)
            .HasColumnType("timestamp without time zone")
            .HasDefaultValueSql("now()");
        builder.HasMany(payment => payment.Refunds)
            .WithOne(refund => refund.Payment)
            .HasForeignKey(refund => refund.PaymentId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class RefundConfiguration : IEntityTypeConfiguration<Refund>
{
    public void Configure(EntityTypeBuilder<Refund> builder)
    {
        builder.ToTable("refunds");
        builder.HasKey(refund => refund.Id);
        builder.Property(refund => refund.Amount).HasPrecision(18, 2).IsRequired();
        builder.Property(refund => refund.Status).HasMaxLength(30).HasDefaultValue(PaymentStatuses.Pending);
        builder.Property(refund => refund.CreatedAt)
            .HasColumnType("timestamp without time zone")
            .HasDefaultValueSql("now()");
    }
}
