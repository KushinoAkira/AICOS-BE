using Microsoft.Extensions.Configuration;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Billing;
using AICOS.ReportingAnalytics.Api;
using AICOS.ReportingAnalytics.Api.Persistence;
using AICOS.Payments.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using FluentAssertions;
namespace AICOS.Services.IntegrationTests;

public sealed class CashierShiftTests
{
    static async Task<JsonElement> Open(SessionBillHarness h,string key="open-cashier-01")
    {
        using var req=new HttpRequestMessage(HttpMethod.Post,"/api/v1/payments/cashier-shifts"){Content=JsonContent.Create(new{branchId=h.Branch,openingCash=100000,note="Test drawer"})};req.Headers.Add("Idempotency-Key",key);
        var response=await h.Cashier.SendAsync(req);response.StatusCode.Should().Be(HttpStatusCode.OK);return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
    [Fact] public async Task Cash_links_once_to_money_shift_and_close_is_immutable_and_retryable()
    {
        using var h=new SessionBillHarness{EnableCashierShifts=true};await h.InitializeAsync();var s=await Open(h);var again=await Open(h);again.GetProperty("id").GetGuid().Should().Be(s.GetProperty("id").GetGuid());
        using var duplicate=new HttpRequestMessage(HttpMethod.Post,"/api/v1/payments/cashier-shifts"){Content=JsonContent.Create(new{branchId=h.Branch,openingCash=0})};duplicate.Headers.Add("Idempotency-Key","another-open-key");(await h.Cashier.SendAsync(duplicate)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        await h.RoundAsync();var draft=await h.BillAsync();await h.CashAsync(draft,"shift-cash-one");await h.CashAsync(draft,"shift-cash-one");
        var url="/api/v1/payments/cashier-shifts/"+s.GetProperty("id").GetGuid();var current=await h.Cashier.GetFromJsonAsync<JsonElement>(url);
        (await h.Cashier.PostAsJsonAsync(url+"/close",new{version=1,countedCash=150000})).StatusCode.Should().Be(HttpStatusCode.Conflict);
        current.GetProperty("cashCollected").GetDecimal().Should().Be(50000);current.GetProperty("expectedCash").GetDecimal().Should().Be(150000);current.GetProperty("qrCollected").ValueKind.Should().Be(JsonValueKind.Null);
        (await h.Cashier.PostAsJsonAsync(url+"/close",new{version=0,countedCash=150000})).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await h.Cashier.PostAsJsonAsync(url+"/close",new{version=current.GetProperty("version").GetInt64(),countedCash=149000})).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using(var riskScope=h.Payments.Services.CreateScope())
        {
            var riskDb=riskScope.ServiceProvider.GetRequiredService<PaymentsDbContext>();var riskBill=await riskDb.SessionBills.SingleAsync();riskBill.NeedsReconciliation=true;await riskDb.SaveChangesAsync();
            (await h.Cashier.GetFromJsonAsync<JsonElement>(url)).GetProperty("canClose").GetBoolean().Should().BeFalse();
            (await h.Cashier.PostAsJsonAsync(url+"/close",new{version=current.GetProperty("version").GetInt64(),countedCash=150000})).StatusCode.Should().Be(HttpStatusCode.Conflict);
            riskBill.NeedsReconciliation=false;await riskDb.SaveChangesAsync();
        }
        var request=new{version=current.GetProperty("version").GetInt64(),countedCash=149000,note="Counted short 1000"};(await h.Cashier.PostAsJsonAsync(url+"/close",request)).StatusCode.Should().Be(HttpStatusCode.OK);(await h.Cashier.PostAsJsonAsync(url+"/close",request)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await h.Cashier.PostAsJsonAsync(url+"/close",new{version=2,countedCash=150000})).StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var scope=h.Payments.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();(await db.Collections.CountAsync()).Should().Be(1);(await db.CashierShifts.SingleAsync()).ExpectedCash.Should().Be(150000);
    }
    [Fact]public async Task Cash_does_not_require_shift_and_foreign_cashier_cannot_read_or_close_drawer()
    {
        using var h=new SessionBillHarness{EnableCashierShifts=true};await h.InitializeAsync();await h.RoundAsync();await h.CashAsync(await h.BillAsync());var s=await Open(h);var id=s.GetProperty("id").GetGuid();
        s.GetProperty("cashCollected").GetDecimal().Should().Be(0);
        using var other=h.Payments.As(AicosRoles.Cashier,h.Branch,Guid.NewGuid());using var bar=h.Payments.As(AicosRoles.Barista,h.Branch);
        (await other.GetAsync("/api/v1/payments/cashier-shifts/"+id)).StatusCode.Should().Be(HttpStatusCode.Forbidden);(await bar.GetAsync("/api/v1/payments/cashier-shifts")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
    [Fact]public async Task Reporting_inbox_is_idempotent_scoped_and_never_changes_order_revenue()
    {
        var key=new string('k',40);using var f=new ServiceFactory<ReportingAnalyticsService,ReportingAnalyticsDbContext>(settings:new Dictionary<string,string?>{{"CashierShifts:Enabled","true"},{"CashierShifts:MachineKey",key}});
        var tenant=ServiceFactory<ReportingAnalyticsService,ReportingAnalyticsDbContext>.DefaultTenantId;var branch=f.Branches.Add(tenant);
        using var internalClient=f.CreateClient();var r=new CollectionReceipt(Guid.NewGuid(),tenant,branch,Guid.NewGuid(),null,Guid.NewGuid(),null,"Cash",210000,DateTime.UtcNow,"TEST-210000");
        (await internalClient.PostAsJsonAsync("/internal/collections",r)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);internalClient.DefaultRequestHeaders.Add(CollectionMachineAuth.Header,key);
        (await internalClient.PostAsJsonAsync("/internal/collections",r)).StatusCode.Should().Be(HttpStatusCode.OK);(await internalClient.PostAsJsonAsync("/internal/collections",r)).StatusCode.Should().Be(HttpStatusCode.OK);(await internalClient.PostAsJsonAsync("/internal/collections",r with{Amount=1})).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await internalClient.PostAsJsonAsync("/internal/collections",r with{Method=null!})).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var manager=f.As(AicosRoles.BranchManager,branch);var report=await manager.GetFromJsonAsync<JsonElement>("/api/v1/reporting/collections?branchId="+branch);report.GetProperty("confirmedCollected").GetDecimal().Should().Be(210000);report.GetProperty("transactions").GetInt32().Should().Be(1);report.GetProperty("netSales").ValueKind.Should().Be(JsonValueKind.Null);
        using var scope=f.Services.CreateScope();(await scope.ServiceProvider.GetRequiredService<ReportingAnalyticsDbContext>().OrderFacts.CountAsync()).Should().Be(0);
    }

    [Fact] public async Task Reconciliation_recovers_old_receipt_without_changing_closed_drawer()
    {
        using var h=new SessionBillHarness{EnableCashierShifts=true};await h.InitializeAsync();var shift=await Open(h);await h.RoundAsync();await h.CashAsync(await h.BillAsync());
        var id=shift.GetProperty("id").GetGuid();var url="/api/v1/payments/cashier-shifts/"+id;
        (await h.Cashier.PostAsJsonAsync(url+"/close",new{version=2,countedCash=150000})).StatusCode.Should().Be(HttpStatusCode.OK);
        using var scope=h.Payments.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        db.Collections.Remove(await db.Collections.SingleAsync());await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<AICOS.Payments.Api.Features.CashierShifts.CollectionDispatcher>().RunAsync(default);
        var repaired=await db.Collections.SingleAsync();repaired.ShiftId.Should().BeNull();JsonSerializer.Deserialize<CollectionReceipt>(repaired.Payload)!.Recovered.Should().BeTrue();
        (await db.CashierShifts.SingleAsync()).ExpectedCash.Should().Be(150000);(await db.CashierShifts.SingleAsync()).CountedCash.Should().Be(150000);
    }

    [Fact] public async Task Reporting_timeout_keeps_worker_alive_and_does_not_ack_receipt()
    {
        using var h=new SessionBillHarness{EnableCashierShifts=true};await h.InitializeAsync();await h.RoundAsync();await h.CashAsync(await h.BillAsync());
        using var scope=h.Payments.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var config=new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"CashierShifts:Enabled","true"},{"CashierShifts:MachineKey",new string('x',40)},{"Services:Reporting:BaseUrl","http://reporting.invalid"}}).Build();
        var timeout=new TimeoutClients();var services=new ServiceCollection();services.AddScoped(_=>db);services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(config);services.AddSingleton<IHttpClientFactory>(timeout);services.AddScoped<AICOS.Payments.Api.Features.CashierShifts.CollectionDispatcher>();
        using var provider=services.BuildServiceProvider();using var worker=new AICOS.Payments.Api.Features.CashierShifts.CollectionWorker(provider.GetRequiredService<IServiceScopeFactory>(),config,Microsoft.Extensions.Logging.Abstractions.NullLogger<AICOS.Payments.Api.Features.CashierShifts.CollectionWorker>.Instance);
        await worker.StartAsync(default);await Task.Delay(100);timeout.Calls.Should().Be(1);worker.ExecuteTask!.IsCompleted.Should().BeFalse();
        using var verify=h.Payments.Services.CreateScope();
        (await verify.ServiceProvider.GetRequiredService<PaymentsDbContext>().Collections.SingleAsync()).DeliveredAtUtc.Should().BeNull();await worker.StopAsync(default);
    }
    private sealed class TimeoutClients:IHttpClientFactory
    {
        public int Calls;
        public HttpClient CreateClient(string name)=>new(new TimeoutHandler(()=>Calls++));
    }
    private sealed class TimeoutHandler(Action called):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){called();throw new TaskCanceledException("Test HTTP timeout, not host cancellation");}
    }
}
