using System.Text.Json;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Billing;
using AICOS.ReportingAnalytics.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace AICOS.ReportingAnalytics.Api.Features.Reports;

/// <summary>Receipt inbox and collection read model. Never added to OrderFacts revenue totals.</summary>
public sealed class CollectionFact
{
    public Guid Id {get;set;}
    public Guid TenantId {get;set;}
    public Guid BranchId {get;set;}
    public string Method {get;set;}="";
    public decimal Amount {get;set;}
    public DateTime PaidAtUtc {get;set;}
    public string Payload {get;set;}="";
}
internal sealed class CollectionFactMapping:IEntityTypeConfiguration<CollectionFact>
{
    public void Configure(EntityTypeBuilder<CollectionFact>b){b.ToTable("collection_facts",t=>t.HasCheckConstraint("ck_collection_fact_amount","amount > 0"));b.HasKey(x=>x.Id);b.Property(x=>x.Id).ValueGeneratedNever();b.Property(x=>x.Amount).HasPrecision(18,0);b.Property(x=>x.Payload).HasColumnType("jsonb");b.Property(x=>x.Method).HasMaxLength(20);b.HasIndex(x=>new{x.TenantId,x.BranchId,x.PaidAtUtc});}
}
public static class CollectionReports
{
    public static void MapCollections(this WebApplication app,RouteGroupBuilder api)
    {
        app.MapPost("/internal/collections",ReceiveAsync).AllowAnonymous();
        api.MapGet("/collections",ReadAsync).RequireAuthorization(b=>b.RequireRole(AicosRoles.Cashier,AicosRoles.BranchManager,AicosRoles.BrandAdmin))
            .AddEndpointFilter(async(c,next)=>c.HttpContext.RequestServices.GetRequiredService<IConfiguration>().GetValue<bool>("CashierShifts:Enabled")?await next(c):Results.StatusCode(503));
    }
    static async Task<IResult> ReceiveAsync(CollectionReceipt r,HttpContext http,IConfiguration config,ReportingAnalyticsDbContext db,CancellationToken ct)
    {
        if(!CollectionMachineAuth.Valid(http,config))return Results.Unauthorized();
        if(r.EventId==Guid.Empty||r.TenantId==Guid.Empty||r.BranchId==Guid.Empty||r.Amount<=0||r.Amount>1_000_000_000m||r.Amount!=decimal.Truncate(r.Amount)||r.PaidAtUtc.Kind!=DateTimeKind.Utc||string.IsNullOrWhiteSpace(r.Method)||r.Method.Length>20||string.IsNullOrWhiteSpace(r.Reference)||r.Reference.Length>64||(r.BillId==null)==(r.OrderId==null))throw new ValidationFailedException("receipt","Invalid confirmed collection.");
        var payload=JsonSerializer.Serialize(r);
        var strategy=db.Database.CreateExecutionStrategy();return await strategy.ExecuteAsync(async()=>{
            await using var tx=db.Database.IsRelational()?await db.Database.BeginTransactionAsync(ct):null;
            if(db.Database.IsRelational())await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"collection:"+r.EventId},0))",ct);
            var old=await db.CollectionFacts.SingleOrDefaultAsync(x=>x.Id==r.EventId,ct);
            if(old!=null&&JsonSerializer.Deserialize<CollectionReceipt>(old.Payload)!=r)throw new ConflictException("Receipt identity changed.","collections.identity_conflict");
            if(old==null){db.CollectionFacts.Add(new(){Id=r.EventId,TenantId=r.TenantId,BranchId=r.BranchId,Method=r.Method,Amount=r.Amount,PaidAtUtc=r.PaidAtUtc,Payload=payload});await db.SaveChangesAsync(ct);}
            if(tx!=null)await tx.CommitAsync(ct);return Results.Ok(new{r.EventId});
        });
    }
    static async Task<IResult> ReadAsync(Guid? branchId,DateTime? from,DateTime? to,ReportingAnalyticsDbContext db,ICurrentUser user,IBranchDirectory directory,CancellationToken ct)
    {
        var tenant=TenantScope.Require(user);var branch=await BranchScope.ResolveAsync(user,directory,branchId,false,ct);
        var end=to??DateTime.UtcNow;var start=from??end.AddDays(-31);if(end<=start||end-start>TimeSpan.FromDays(93))throw new ValidationFailedException("range","Use an increasing range of at most 93 days.");
        var q=db.CollectionFacts.AsNoTracking().Where(x=>x.TenantId==tenant&&x.BranchId==branch&&x.PaidAtUtc>=start&&x.PaidAtUtc<end);
        var totals=await q.GroupBy(x=>x.Method).Select(g=>new{Method=g.Key,Amount=g.Sum(x=>x.Amount),Count=g.Count()}).ToListAsync(ct);
        var rows=await q.OrderByDescending(x=>x.PaidAtUtc).Take(100).ToListAsync(ct);
        return Results.Ok(new{BranchId=branch,From=start,To=end,ConfirmedCollected=totals.Sum(x=>x.Amount),CashCollected=totals.Where(x=>x.Method=="Cash").Sum(x=>x.Amount),QrCollected=totals.Where(x=>x.Method=="QR"||x.Method=="VietQR"||x.Method=="Mock").Sum(x=>x.Amount),OtherCollected=totals.Where(x=>x.Method!="Cash"&&x.Method!="QR"&&x.Method!="VietQR"&&x.Method!="Mock").Sum(x=>x.Amount),Transactions=totals.Sum(x=>x.Count),RefundedAmount=(decimal?)null,NetSales=(decimal?)null,Metric="CONFIRMED_COLLECTIONS_NOT_NET_SALES",Rows=rows.Select(x=>JsonSerializer.Deserialize<CollectionReceipt>(x.Payload)),HasMore=totals.Sum(x=>x.Count)>100});
    }
}
