using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace AICOS.Payments.Api.Features.SplitBilling;
public sealed record SplitSlot(Guid OrderItemId,int Quantity);
public sealed record SplitGroup(Guid Id,string Name,IReadOnlyList<SplitSlot> Items);
public sealed record SaveSplitPlan(Guid SessionId,long Version,string Fingerprint,IReadOnlyList<SplitGroup> Groups);
public sealed class SplitPlan
{
    public Guid Id {get;set;}
    public Guid TenantId {get;set;}
    public Guid BranchId {get;set;}
    public Guid SessionId {get;set;}
    public long Version {get;set;}
    public string Fingerprint {get;set;}="";
    public string QuoteJson {get;set;}="{}";
    public string GroupsJson {get;set;}="[]";
    public Guid UpdatedBy {get;set;}
    public DateTime UpdatedAtUtc {get;set;}
}
public sealed class SplitPlanOperation
{
    public Guid Id {get;set;}
    public Guid PlanId {get;set;}
    public Guid ActorId {get;set;}
    public string RequestHash {get;set;}="";
    public string BeforeJson {get;set;}="[]";
    public string AfterJson {get;set;}="[]";
    public DateTime CreatedAtUtc {get;set;}
}
internal sealed class SplitPlanMapping:IEntityTypeConfiguration<SplitPlan>
{
    public void Configure(EntityTypeBuilder<SplitPlan>b){b.ToTable("bill_split_plans");b.HasKey(x=>x.Id);b.Property(x=>x.Id).ValueGeneratedNever();b.Property(x=>x.Fingerprint).HasMaxLength(64);b.Property(x=>x.QuoteJson).HasColumnType("jsonb");b.Property(x=>x.GroupsJson).HasColumnType("jsonb");b.HasIndex(x=>new{x.TenantId,x.SessionId}).IsUnique();}
}
internal sealed class SplitPlanOperationMapping:IEntityTypeConfiguration<SplitPlanOperation>
{
    public void Configure(EntityTypeBuilder<SplitPlanOperation>b){b.ToTable("bill_split_operations");b.HasKey(x=>x.Id);b.Property(x=>x.Id).ValueGeneratedNever();b.Property(x=>x.RequestHash).HasMaxLength(64);b.Property(x=>x.BeforeJson).HasColumnType("jsonb");b.Property(x=>x.AfterJson).HasColumnType("jsonb");b.HasOne<SplitPlan>().WithMany().HasForeignKey(x=>x.PlanId).OnDelete(DeleteBehavior.Restrict);}
}
