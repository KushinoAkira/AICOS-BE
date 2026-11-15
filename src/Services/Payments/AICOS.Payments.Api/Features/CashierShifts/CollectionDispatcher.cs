using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Billing;
using AICOS.Payments.Api.Domain;
using AICOS.Payments.Api.Features.SessionBilling;
using AICOS.Payments.Api.Infrastructure;
using AICOS.Payments.Api.Persistence;
using Microsoft.EntityFrameworkCore;
namespace AICOS.Payments.Api.Features.CashierShifts;

public sealed class CollectionDispatcher(PaymentsDbContext db,IHttpClientFactory clients,IConfiguration config)
{
    public async Task RunAsync(CancellationToken ct)
    {
        if(!db.CashierEnabled)return;
        // Re-scan every missing durable source, without an age cutoff or a lossy timestamp watermark.
        var old=await db.Payments.AsNoTracking().Where(p=>(p.Status==PaymentStatus.Succeeded||p.Status==PaymentStatus.PartiallyRefunded||p.Status==PaymentStatus.Refunded)&&p.PaidAtUtc!=null&&!db.Collections.Any(c=>c.Id==p.Id)).OrderBy(p=>p.PaidAtUtc).Take(100).ToListAsync(ct);
        foreach(var p in old)await PaymentOrderLock.RunAsync(db,p.TenantId,p.OrderId,async()=>{
            await CashierLedger.CaptureAsync(db,new(p.Id,p.TenantId,p.BranchId,null,p.OrderId,p.CashierUserId,null,p.Method,p.Amount,p.PaidAtUtc!.Value,p.TransactionRef,true),ct);await db.SaveChangesAsync(ct);return true;
        },ct);
        if(db.BillEnabled)
        {
            var bills=await db.SessionBills.AsNoTracking().Where(b=>(b.Status=="PAID"||b.Status=="CLOSED")&& !b.NeedsReconciliation &&b.PaidPaymentId!=null&&!db.Collections.Any(c=>c.Id==b.PaidPaymentId)).OrderBy(b=>b.PaidAtUtc).Take(100).ToListAsync(ct);
            foreach(var b in bills)await BillLock.RunAsync(db,b.Id,async()=>{
                var a=await db.BillAttempts.AsNoTracking().SingleAsync(a=>a.Id==b.PaidPaymentId,ct);
                if(a.Status!="SUCCEEDED"||a.Amount!=b.Total||a.PaidAtUtc==null||b.NeedsReconciliation)return false;
                await CashierLedger.CaptureAsync(db,new(a.Id,b.TenantId,b.BranchId,b.Id,null,a.Method=="Cash"?a.CreatedByUserId:null,null,a.Method,a.Amount,a.PaidAtUtc.Value,a.TransactionRef,true),ct);await db.SaveChangesAsync(ct);return true;
            },ct);
        }
        var endpoint=config["Services:Reporting:BaseUrl"];var key=config["CashierShifts:MachineKey"];
        if(!Uri.TryCreate(endpoint,UriKind.Absolute,out var uri)||key is not {Length:>=32})return;
        var rows=await db.Collections.Where(x=>x.DeliveredAtUtc==null).OrderBy(x=>x.PaidAtUtc).Take(100).ToListAsync(ct);
        using var client=clients.CreateClient("collections");client.Timeout=TimeSpan.FromSeconds(10);
        foreach(var row in rows)
        {
            using var request=new HttpRequestMessage(HttpMethod.Post,new Uri(uri,"/internal/collections")){Content=JsonContent.Create(JsonSerializer.Deserialize<CollectionReceipt>(row.Payload))};request.Headers.Add(CollectionMachineAuth.Header,key);
            using var response=await client.SendAsync(request,ct);
            // Only the receiver's stable receipt acknowledgement proves a committed inbox write.
            if(response.IsSuccessStatusCode&&(await response.Content.ReadFromJsonAsync<CollectionAck>(cancellationToken:ct))?.EventId==row.Id){row.DeliveredAtUtc=DateTime.UtcNow;await db.SaveChangesAsync(ct);}
        }
    }
    private sealed record CollectionAck(Guid EventId);
}
public sealed class CollectionWorker(IServiceScopeFactory scopes,IConfiguration config,ILogger<CollectionWorker> log):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(10));do{
            if(config.GetValue<bool>("CashierShifts:Enabled")&&config.GetValue("CashierShifts:DispatcherEnabled",true))try{await using var scope=scopes.CreateAsyncScope();await scope.ServiceProvider.GetRequiredService<CollectionDispatcher>().RunAsync(ct);}catch(Exception e)when(!ct.IsCancellationRequested){log.LogWarning("Collection reconciliation will retry ({ErrorType}).",e.GetType().Name);}
        }while(await timer.WaitForNextTickAsync(ct));
    }
}
