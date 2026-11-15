using System.Text.Json;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.Contracts.Billing;
using AICOS.Payments.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace AICOS.Payments.Api.Features.CashierShifts;

public sealed class CashierShift
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid BranchId { get; set; }
    public Guid CashierId { get; set; }
    public string OpenKey { get; set; } = "";
    public decimal OpeningCash { get; set; }
    public string? Note { get; set; }
    public DateTime OpenedAtUtc { get; set; }
    public DateTime? ClosedAtUtc { get; set; }
    public decimal? CountedCash { get; set; }
    public decimal? ExpectedCash { get; set; }
    public string? ClosingNote { get; set; }
    public long Version { get; set; } = 1;
}
/// <summary>Immutable collection proof and retryable Reporting outbox, written with the payment transaction.</summary>
public sealed class CollectionOutbox
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid BranchId { get; set; }
    public Guid? CashierId { get; set; }
    public Guid? ShiftId { get; set; }
    public decimal Amount { get; set; }
    public string Method { get; set; } = "";
    public string Payload { get; set; } = "";
    public DateTime PaidAtUtc { get; set; }
    public DateTime? DeliveredAtUtc { get; set; }
}
internal sealed class CashierShiftMapping : IEntityTypeConfiguration<CashierShift>
{
    public void Configure(EntityTypeBuilder<CashierShift> b)
    {
        b.ToTable("cashier_shifts", t => t.HasCheckConstraint("ck_cashier_shift_money", "opening_cash >= 0 AND (counted_cash IS NULL OR counted_cash >= 0)"));
        b.HasKey(x=>x.Id); b.Property(x=>x.Id).ValueGeneratedNever(); b.Property(x=>x.OpeningCash).HasPrecision(18,0); b.Property(x=>x.CountedCash).HasPrecision(18,0); b.Property(x=>x.ExpectedCash).HasPrecision(18,0);
        b.Property(x=>x.OpenKey).HasMaxLength(64); b.Property(x=>x.Note).HasMaxLength(500); b.Property(x=>x.ClosingNote).HasMaxLength(500);
        b.HasIndex(x=>new {x.TenantId,x.CashierId}).IsUnique().HasFilter("closed_at_utc IS NULL"); b.HasIndex(x=>new {x.TenantId,x.CashierId,x.OpenKey}).IsUnique();
    }
}
internal sealed class CollectionOutboxMapping : IEntityTypeConfiguration<CollectionOutbox>
{
    public void Configure(EntityTypeBuilder<CollectionOutbox> b)
    {
        b.ToTable("collection_outbox", t=>t.HasCheckConstraint("ck_collection_amount", "amount > 0")); b.HasKey(x=>x.Id); b.Property(x=>x.Id).ValueGeneratedNever(); b.Property(x=>x.Amount).HasPrecision(18,0); b.Property(x=>x.Payload).HasColumnType("jsonb"); b.Property(x=>x.Method).HasMaxLength(20);
        b.HasIndex(x=>x.ShiftId); b.HasIndex(x=>x.PaidAtUtc).HasFilter("delivered_at_utc IS NULL");
        b.HasOne<CashierShift>().WithMany().HasForeignKey(x=>x.ShiftId).OnDelete(DeleteBehavior.Restrict);
    }
}
public static class CashierLedger
{
    public static async Task ActorLockAsync(PaymentsDbContext db, Guid tenant, Guid cashier, CancellationToken ct)
    {
        if (db.Database.IsRelational()) await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"cashier:"+tenant+":"+cashier},0))",ct);
    }
    public static async Task CaptureAsync(PaymentsDbContext db, CollectionReceipt receipt, CancellationToken ct)
    {
        if (!db.CashierEnabled)
        {
            if (db.RequireCashierShift && !receipt.Recovered && receipt.Method == "Cash")
                throw new ConflictException("Cashier shifts must be enabled before requiring an open drawer.", "cashier.shift_unavailable");
            return;
        }
        if (db.Collections.Local.Any(x=>x.Id==receipt.EventId) || await db.Collections.AnyAsync(x=>x.Id==receipt.EventId,ct)) return;
        Guid? shift = null;
        if (!receipt.Recovered && receipt.Method == "Cash" && receipt.CashierId is {} actor)
        {
            await ActorLockAsync(db,receipt.TenantId,actor,ct);
            var open = await db.CashierShifts.SingleOrDefaultAsync(x=>x.TenantId==receipt.TenantId && x.BranchId==receipt.BranchId && x.CashierId==actor && x.ClosedAtUtc==null,ct);
            if (db.RequireCashierShift && open is null)
                throw new ConflictException("Open your own cash drawer in this branch before collecting cash.", "cashier.open_shift_required");
            shift = open?.Id;
            if(open is not null) open.Version++; // A new receipt invalidates a previously displayed drawer count.

        }
        if (db.RequireCashierShift && !receipt.Recovered && receipt.Method == "Cash" && receipt.CashierId is null)
            throw new ConflictException("A cashier identity is required for cash collection.", "cashier.open_shift_required");
        receipt = receipt with { ShiftId = shift };
        db.Collections.Add(new CollectionOutbox {Id=receipt.EventId,TenantId=receipt.TenantId,BranchId=receipt.BranchId,CashierId=receipt.CashierId,ShiftId=shift,Amount=receipt.Amount,Method=receipt.Method,PaidAtUtc=receipt.PaidAtUtc,Payload=JsonSerializer.Serialize(receipt)});
    }
}
