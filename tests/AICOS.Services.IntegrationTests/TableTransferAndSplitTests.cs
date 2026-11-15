using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;
using AICOS.Ordering.Api.Features.Qr;
using AICOS.Ordering.Api.Persistence;
using AICOS.Payments.Api.Features.SplitBilling;
using AICOS.Payments.Api.Persistence;
using AICOS.KitchenOperations.Api.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
namespace AICOS.Services.IntegrationTests;
public sealed class TableTransferAndSplitTests
{
    static async Task<HttpResponseMessage> Move(SessionBillHarness h,string target,string key="move-test-one")
    {
        using var req=new HttpRequestMessage(HttpMethod.Post,$"/api/v1/orders/qr/sessions/{h.Session}/transfer"){Content=JsonContent.Create(new{expectedTableId=h.Table,targetQrToken=target})};req.Headers.Add("Idempotency-Key",key);return await h.OrderCashier.SendAsync(req);
    }
    [Fact]public async Task Transfer_preserves_session_and_bill_and_retries_offline_routing_without_duplicate_tickets()
    {
        using var h=new SessionBillHarness{EnableTableTransfer=true};await h.InitializeAsync();var order=await h.RoundAsync();var bill=await h.BillAsync();var target=h.AddTransferTarget("A02");h.KitchenOffline=true;
        var r=await Move(h,target);r.StatusCode.Should().Be(HttpStatusCode.Accepted);var pending=await r.Content.ReadFromJsonAsync<JsonElement>();var op=pending.GetProperty("operationId").GetGuid();pending.GetProperty("syncStatus").GetString().Should().Be("PENDING_SYNC");
        (await Move(h,target)).StatusCode.Should().Be(HttpStatusCode.Accepted);(await h.BillAsync()).Id.Should().Be(bill.Id);(await h.BillAsync()).Quote.Total.Should().Be(50000);
        h.KitchenOffline=false;using(var scope=h.Ordering.Services.CreateScope()){(await scope.ServiceProvider.GetRequiredService<TableTransferDispatcher>().TryAsync(op,default)).Should().BeTrue();var db=scope.ServiceProvider.GetRequiredService<OrderingDbContext>();(await db.Orders.CountAsync()).Should().Be(1);(await db.TableTransfers.CountAsync()).Should().Be(1);}
        using(var scope=h.Kitchen.Services.CreateScope()){var db=scope.ServiceProvider.GetRequiredService<KitchenOperationsDbContext>();(await db.Tickets.SingleAsync()).TableNumber.Should().Be("A02");(await db.Tickets.SingleAsync()).TableTransferVersion.Should().Be(1);}
        (await Move(h,target)).StatusCode.Should().Be(HttpStatusCode.OK);
        var history=await h.OrderCashier.GetFromJsonAsync<JsonElement>($"/api/v1/orders/qr/sessions/{h.Session}/transfers");history.GetArrayLength().Should().Be(1);history[0].GetProperty("actorId").GetGuid().Should().Be(h.CashierId);
    }
    [Fact]public async Task Transfer_is_blocked_after_paid_and_barista_cannot_move_or_read_history()
    {
        using var h=new SessionBillHarness{EnableTableTransfer=true};await h.InitializeAsync();await h.RoundAsync();var target=h.AddTransferTarget("A02");await h.CashAsync(await h.BillAsync());(await Move(h,target)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var bar=h.Ordering.As(AicosRoles.Barista,h.Branch);(await bar.GetAsync($"/api/v1/orders/qr/sessions/{h.Session}/transfers")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var scope=h.Ordering.Services.CreateScope();(await scope.ServiceProvider.GetRequiredService<OrderingDbContext>().TableTransfers.CountAsync()).Should().Be(0);
    }
    [Fact]public async Task Kitchen_transfer_fencing_ignores_stale_and_duplicate_routing_without_resetting_state()
    {
        using var h=new SessionBillHarness{EnableTableTransfer=true};await h.InitializeAsync();var order=await h.RoundAsync();using var machine=h.Kitchen.CreateClient();var body=new DurableTableTransfer(Guid.NewGuid(),h.Tenant,h.Branch,h.Session,2,h.Table,Guid.NewGuid(),"NEW",new[]{order});
        (await machine.PostAsJsonAsync("/internal/table-transfers",body)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);machine.DefaultRequestHeaders.Add(TableTransferMachineAuth.Header,h.MachineKey);
        (await machine.PostAsJsonAsync("/internal/table-transfers",body)).StatusCode.Should().Be(HttpStatusCode.OK);(await machine.PostAsJsonAsync("/internal/table-transfers",body)).StatusCode.Should().Be(HttpStatusCode.OK);(await machine.PostAsJsonAsync("/internal/table-transfers",body with{Version=1,TargetCode="OLD"})).StatusCode.Should().Be(HttpStatusCode.OK);
        using var scope=h.Kitchen.Services.CreateScope();var ticket=await scope.ServiceProvider.GetRequiredService<KitchenOperationsDbContext>().Tickets.SingleAsync();ticket.TableNumber.Should().Be("NEW");ticket.TableTransferVersion.Should().Be(2);ticket.RecoveryHoldReason.Should().BeNull();
    }
    [Fact]public async Task Split_groups_validate_partial_quantities_merge_without_loss_and_do_not_create_another_root_bill()
    {
        using var h=new SessionBillHarness{EnableSplitBill=true};await h.InitializeAsync();
        using(var req=new HttpRequestMessage(HttpMethod.Post,"/api/v1/orders"){Content=JsonContent.Create(new{branchId=h.Branch,type="DineIn",tableId=h.Table,tableNumber="A01",tableQrToken=h.Qr,expectedSessionId=h.Session,items=new[]{new{productId=h.Product,quantity=3}}})}){req.Headers.Add("Idempotency-Key","split-round-one");(await h.OrderCashier.SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.Created);}
        var bill=await h.BillAsync();var item=bill.Quote.Lines.Single().OrderItemId!.Value;var a=Guid.NewGuid();var b=Guid.NewGuid();var path=$"/api/v1/payments/bills/{bill.Id}/split-plan";
        async Task<HttpResponseMessage> Save(long version,SplitGroup[] groups,string key){using var req=new HttpRequestMessage(HttpMethod.Put,path){Content=JsonContent.Create(new SaveSplitPlan(h.Session,version,bill.Fingerprint,groups))};req.Headers.Add("Idempotency-Key",key);return await h.Cashier.SendAsync(req);}
        var invalid=new[]{new SplitGroup(a,"A",new[]{new SplitSlot(item,4)})};(await Save(0,invalid,"split-invalid-key")).StatusCode.Should().Be(HttpStatusCode.Conflict);
        var groups=new[]{new SplitGroup(a,"A",new[]{new SplitSlot(item,2)}),new SplitGroup(b,"B",new[]{new SplitSlot(item,1)})};var response=await Save(0,groups,"split-save-key");response.StatusCode.Should().Be(HttpStatusCode.OK);var view=await response.Content.ReadFromJsonAsync<JsonElement>();view.GetProperty("allocationComplete").GetBoolean().Should().BeTrue();view.GetProperty("allocatedSubtotal").GetDecimal().Should().Be(150000);view.GetProperty("paymentAvailable").GetBoolean().Should().BeFalse();view.GetProperty("groups")[0].GetProperty("amountDue").ValueKind.Should().Be(JsonValueKind.Null);
        var merged=new[]{new SplitGroup(Guid.NewGuid(),"AB",new[]{new SplitSlot(item,3)})};(await Save(1,merged,"split-merge-key")).StatusCode.Should().Be(HttpStatusCode.OK);(await Save(1,merged,"split-merge-key")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Save(2,Array.Empty<SplitGroup>(),"split-delete-key")).StatusCode.Should().Be(HttpStatusCode.Conflict);
        using(var scope=h.Payments.Services.CreateScope()){var db=scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();(await db.SessionBills.CountAsync()).Should().Be(0);(await db.SplitOperations.CountAsync()).Should().Be(2);}
        await h.CashAsync(bill);(await Save(2,merged,"split-after-paid")).StatusCode.Should().Be(HttpStatusCode.Conflict);
        using(var scope=h.Payments.Services.CreateScope())(await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().SessionBills.CountAsync()).Should().Be(1);
    }
}
