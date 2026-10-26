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
        builder.HasIndex(bill => bill.SessionId).IsUnique().HasFilter("session_id IS NOT NULL");
        builder.HasIndex(bill => bill.OrderId).IsUnique().HasFilter("order_id IS NOT NULL");
        builder.HasIndex(bill => bill.BranchId);
        builder.Property(bill => bill.Status).HasMaxLength(30).HasDefaultValue(BillStatuses.Unpaid);
        builder.Property(bill => bill.PrintStatus).HasMaxLength(30).HasDefaultValue(BillPrintStatuses.NotIssued);
        builder.Property(bill => bill.TotalAmount).HasPrecision(18, 2).IsRequired();
        builder.Property(bill => bill.Version).HasDefaultValue(0L).IsConcurrencyToken();
        ConfigureTimestamp(builder.Property(bill => bill.CreatedAt), true);
        ConfigureTimestamp(builder.Property(bill => bill.PrintedAt));
        ConfigureTimestamp(builder.Property(bill => bill.ClosedAt));
        builder.HasMany(bill => bill.Items).WithOne(item => item.Bill)
            .HasForeignKey(item => item.BillId).OnDelete(DeleteBehavior.NoAction);
        builder.HasMany(bill => bill.Payments).WithOne(payment => payment.Bill)
            .HasForeignKey(payment => payment.BillId).OnDelete(DeleteBehavior.NoAction);
    }

    private static void ConfigureTimestamp(PropertyBuilder<DateTime?> property, bool defaultNow = false)
    {
        property.HasColumnType("timestamp without time zone");
        if (defaultNow) property.HasDefaultValueSql("now()");
    }
}

internal sealed class BillItemConfiguration : IEntityTypeConfiguration<BillItem>
{
    public void Configure(EntityTypeBuilder<BillItem> builder)
    {
        builder.ToTable("bill_items");
        builder.HasKey(item => item.Id);
        builder.HasIndex(item => item.BillId);
        builder.HasIndex(item => item.OrderItemId);
        builder.Property(item => item.Amount).HasPrecision(18, 2).IsRequired();
    }
}

internal sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        builder.ToTable("payments");
        builder.HasKey(payment => payment.Id);
        builder.HasIndex(payment => payment.BillId);
        builder.Property(payment => payment.Method).HasMaxLength(30).IsRequired();
        builder.Property(payment => payment.Status).HasMaxLength(30).HasDefaultValue(PaymentStatuses.Pending);
        builder.Property(payment => payment.Amount).HasPrecision(18, 2).IsRequired();
        builder.Property(payment => payment.TransactionReference).HasColumnName("transaction_ref").HasMaxLength(255);
        builder.HasIndex(payment => payment.TransactionReference).IsUnique();
        builder.Property(payment => payment.EvidenceReference).HasMaxLength(255);
        builder.Property(payment => payment.StatusReason).HasMaxLength(500);
        builder.Property(payment => payment.Version).HasDefaultValue(0L).IsConcurrencyToken();
        ConfigureTimestamp(builder.Property(payment => payment.PaidAt));
        ConfigureTimestamp(builder.Property(payment => payment.ConfirmedAt));
        ConfigureTimestamp(builder.Property(payment => payment.CreatedAt), true);
        builder.HasMany(payment => payment.Events).WithOne(paymentEvent => paymentEvent.Payment)
            .HasForeignKey(paymentEvent => paymentEvent.PaymentId).OnDelete(DeleteBehavior.NoAction);
        builder.HasMany(payment => payment.Refunds).WithOne(refund => refund.Payment)
            .HasForeignKey(refund => refund.PaymentId).OnDelete(DeleteBehavior.NoAction);
    }

    private static void ConfigureTimestamp(PropertyBuilder<DateTime?> property, bool defaultNow = false)
    {
        property.HasColumnType("timestamp without time zone");
        if (defaultNow) property.HasDefaultValueSql("now()");
    }
}

internal sealed class PaymentEventConfiguration : IEntityTypeConfiguration<PaymentEvent>
{
    public void Configure(EntityTypeBuilder<PaymentEvent> builder)
    {
        builder.ToTable("payment_events");
        builder.HasKey(paymentEvent => paymentEvent.Id);
        builder.HasIndex(paymentEvent => paymentEvent.RequestId).IsUnique();
        builder.HasIndex(paymentEvent => new { paymentEvent.PaymentId, paymentEvent.OccurredAt });
        builder.Property(paymentEvent => paymentEvent.EventType).HasMaxLength(30).IsRequired();
        builder.Property(paymentEvent => paymentEvent.FromStatus).HasMaxLength(30);
        builder.Property(paymentEvent => paymentEvent.ToStatus).HasMaxLength(30).IsRequired();
        builder.Property(paymentEvent => paymentEvent.EvidenceReference).HasMaxLength(255);
        builder.Property(paymentEvent => paymentEvent.Reason).HasMaxLength(500);
        builder.Property(paymentEvent => paymentEvent.CorrelationId).HasMaxLength(100);
        builder.Property(paymentEvent => paymentEvent.OccurredAt)
            .HasColumnType("timestamp without time zone").HasDefaultValueSql("now()");
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
            .HasColumnType("timestamp without time zone").HasDefaultValueSql("now()");
    }
}
