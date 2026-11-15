using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;
using AICOS.Ordering.Api.Domain.Enums;
using AICOS.Ordering.Api.Persistence;
using Microsoft.EntityFrameworkCore;
namespace AICOS.Ordering.Api.Features.Qr;
public sealed class TableTransferDispatcher(OrderingDbContext db,IHttpClientFactory clients,IConfiguration config)
{
    public async Task<bool> TryAsync(Guid id,CancellationToken ct)
    {
        try{
            var op=await db.TableTransfers.AsNoTracking().SingleAsync(x=>x.Id==id,ct);if(op.SyncedAtUtc!=null)return true;
            var ids=JsonSerializer.Deserialize<TransferSnapshot>(op.OrderIdsJson)!.OrderIds;
            var live=await db.Orders.AsNoTracking().Where(x=>ids.Contains(x.Id)&&x.Status!=OrderStatus.Cancelled&&x.Status!=OrderStatus.NoShow&&x.Status!=OrderStatus.Unfulfillable).Select(x=>x.Id).ToListAsync(ct);
            using var client=clients.CreateClient("table-transfer");client.Timeout=TimeSpan.FromSeconds(8);
            foreach(var chunk in live.Chunk(100)){
                var notice=new DurableTableTransfer(op.Id,op.TenantId,op.BranchId,op.SessionId,op.Version,op.SourceTableId,op.TargetTableId,op.TargetCode,chunk);
                var baseUrl=config["Services:Kitchen:BaseUrl"]??"http://kitchen:8080";
                using var request=new HttpRequestMessage(HttpMethod.Post,new Uri(new Uri(baseUrl),"/internal/table-transfers")){Content=JsonContent.Create(notice)};request.Headers.Add(TableTransferMachineAuth.Header,TableTransferMachineAuth.Key(config));
                using var response=await client.SendAsync(request,ct);if(!response.IsSuccessStatusCode)return false;
                var ack=await response.Content.ReadFromJsonAsync<TableTransferAck>(cancellationToken:ct);if(ack?.OperationId!=id||ack.Version!=op.Version||!ack.Applied)return false;
            }
            var saved=await db.TableTransfers.SingleAsync(x=>x.Id==id,ct);saved.SyncedAtUtc??=DateTime.UtcNow;await db.SaveChangesAsync(ct);return true;
        }catch(Exception)when(!ct.IsCancellationRequested){return false;}
    }
}
public sealed class TableTransferWorker(IServiceScopeFactory scopes,IConfiguration config,ILogger<TableTransferWorker> log):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(5));do{
            if(config.GetValue<bool>("TableTransfer:Enabled")&&config.GetValue("TableTransfer:DispatcherEnabled",true))try{
                await using var scope=scopes.CreateAsyncScope();var db=scope.ServiceProvider.GetRequiredService<OrderingDbContext>();var ids=await db.TableTransfers.AsNoTracking().Where(x=>x.SyncedAtUtc==null).OrderBy(x=>x.CreatedAtUtc).Select(x=>x.Id).Take(20).ToListAsync(ct);
                foreach(var id in ids)await scope.ServiceProvider.GetRequiredService<TableTransferDispatcher>().TryAsync(id,ct);
            }catch(Exception e)when(!ct.IsCancellationRequested){log.LogWarning("Table transfer sync retries ({ErrorType}).",e.GetType().Name);}
        }while(await timer.WaitForNextTickAsync(ct));
    }
}
