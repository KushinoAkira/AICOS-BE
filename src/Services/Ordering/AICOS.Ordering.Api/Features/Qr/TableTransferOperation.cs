using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace AICOS.Ordering.Api.Features.Qr;
public sealed record TransferSnapshot(string SourceCode,Guid[] OrderIds);
public sealed class TableTransferOperation
{
    public Guid Id {get;set;}
    public Guid TenantId {get;set;}
    public Guid BranchId {get;set;}
    public Guid SessionId {get;set;}
    public Guid ActorId {get;set;}
    public Guid SourceTableId {get;set;}
    public Guid TargetTableId {get;set;}
    public string TargetCode {get;set;}="";
    public long Version {get;set;}
    public string RequestHash {get;set;}="";
    public string OrderIdsJson {get;set;}="[]";
    public DateTime CreatedAtUtc {get;set;}
    public DateTime? SyncedAtUtc {get;set;}
}
internal sealed class TableTransferOperationMapping:IEntityTypeConfiguration<TableTransferOperation>
{
    public void Configure(EntityTypeBuilder<TableTransferOperation>b)
    {
        b.ToTable("table_transfer_operations");b.HasKey(x=>x.Id);b.Property(x=>x.Id).ValueGeneratedNever();b.Property(x=>x.TargetCode).HasMaxLength(20);b.Property(x=>x.RequestHash).HasMaxLength(64);b.Property(x=>x.OrderIdsJson).HasColumnType("jsonb");
        b.HasOne<AICOS.Ordering.Api.Domain.TableSession>().WithMany().HasForeignKey(x=>x.SessionId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x=>new{x.TenantId,x.SessionId,x.Version}).IsUnique();b.HasIndex(x=>x.CreatedAtUtc).HasFilter("synced_at_utc IS NULL");
    }
}
