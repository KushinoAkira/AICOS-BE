using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Payments.Api;
using AICOS.Payments.Api.Domain;
using AICOS.Payments.Api.Infrastructure;
using AICOS.Payments.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AICOS.Services.IntegrationTests;

[Collection("Session bill PostgreSQL")]
public sealed class TakeawaySandboxPostgresTests : IAsyncLifetime
{
    private ServiceFactory<PaymentsService,PaymentsDbContext> _factory=null!;
    private HttpClient _cashier=null!;
    private readonly FakeOrderDirectory _orders=new();
    private readonly GatewayDouble _gateway=new();
    private readonly Guid _branch=Guid.NewGuid(), _order=Guid.NewGuid();
    private string Path=>$"/api/v1/payments/staff/takeaway/{_order}/online";
    public async Task InitializeAsync()
    {
        var connection=Environment.GetEnvironmentVariable("AICOS_BILL_TEST_CONNECTION")!;
        await SessionBillHarness.GuardAsync(connection);
        _factory=new(s=>{
            s.RemoveAll<IOrderDirectory>();s.AddSingleton<IOrderDirectory>(_orders);
            s.RemoveAll<GatewayRegistry>();s.AddSingleton(new GatewayRegistry([_gateway]));
            foreach(var d in s.Where(d=>d.ImplementationType?.Name is "PaymentExpiryJob" or "BillOutboxWorker" or "CollectionWorker" or "TakeawayQrRecoveryWorker").ToList())s.Remove(d);
        },connection,settings:new Dictionary<string,string?>{["Database:MigrateOnStartup"]="false",["TakeawayQr:ZaloPaySandboxEnabled"]="true",["Payments:ZaloPay:Live"]="true"},environmentName:"Development");
        using(var scope=_factory.Services.CreateScope())await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Database.MigrateAsync();
        _factory.Branches.Add(ServiceFactory<PaymentsService,PaymentsDbContext>.DefaultTenantId,id:_branch);
        _orders.Set(_order,_branch,50000);
        using var super=_factory.As(AicosRoles.SuperAdmin);
        bool exists;using(var scope=_factory.Services.CreateScope())exists=await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().PaymentProviders.AnyAsync(x=>x.Code=="ZaloPay");
        if(!exists)(await super.PostAsJsonAsync("/api/v1/payments/providers",new{code="ZaloPay",displayName="Scratch sandbox",mode="Sandbox",merchantId="test",apiKey="test-key-one",secretKey="test-key-two"})).EnsureSuccessStatusCode();
        using(var scope=_factory.Services.CreateScope()){
            var db=scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();var provider=await db.PaymentProviders.SingleAsync(x=>x.Code=="ZaloPay");provider.SetEnabled(true);await db.SaveChangesAsync();
        }
        using var admin=_factory.As(AicosRoles.BrandAdmin);
        (await admin.PutAsJsonAsync("/api/v1/payments/merchant-settings",new{providerCode="ZaloPay",isEnabled=true,merchantCode="test"})).EnsureSuccessStatusCode();
        _cashier=_factory.As(AicosRoles.Cashier,_branch);
    }
    public Task DisposeAsync(){_cashier?.Dispose();_factory?.Dispose();return Task.CompletedTask;}
    private Task<HttpResponseMessage> Send(){var r=new HttpRequestMessage(HttpMethod.Post,Path){Content=JsonContent.Create(new{branchId=_branch,amount=50000})};r.Headers.Add("Idempotency-Key","scratch-stable-request");return _cashier.SendAsync(r);}
    [BillPostgresFact]
    public async Task Eight_parallel_requests_create_one_provider_intent_and_one_payment_row()
    {
        var responses=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>Send()));
        responses.Should().OnlyContain(x=>x.IsSuccessStatusCode);_gateway.Calls.Should().Be(1);
        using var scope=_factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Payments.CountAsync(x=>x.OrderId==_order)).Should().Be(1);
    }
    [BillPostgresFact]
    public async Task Lost_create_ack_never_recreates_provider_intent_and_query_recovers_truth()
    {
        _gateway.Lose=true;(await Send()).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var retry=await Send();retry.EnsureSuccessStatusCode();var snapshot=await retry.Content.ReadFromJsonAsync<JsonElement>();
        var id=snapshot.GetProperty("attempt").Id();_gateway.Calls.Should().Be(1);
        var response=await _cashier.PostAsJsonAsync(Path+$"/{id}/query?branchId={_branch}",new{});response.EnsureSuccessStatusCode();
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString().Should().Be("Confirmed");
        (await Send()).EnsureSuccessStatusCode();_gateway.Calls.Should().Be(1);
    }
    private sealed class GatewayDouble:IPaymentGateway
    {
        public string Code=>"ZaloPay";public int Calls;public bool Lose;
        public Task<GatewayCheckout> CreatePaymentAsync(GatewayPaymentRequest r,CancellationToken ct){Interlocked.Increment(ref Calls);if(Lose){Lose=false;throw new HttpRequestException("Controlled lost provider ACK");}return Task.FromResult(new GatewayCheckout("https://qcgateway.zalopay.vn/openinapp?order="+r.TransactionRef));}
        public Task<GatewayStatus> QueryAsync(Payment payment,CancellationToken ct)=>Task.FromResult(new GatewayStatus(ProviderState.Succeeded,"test-proof",null));
        public Task<GatewayRefundResult> RefundAsync(Payment payment,decimal amount,string reason,CancellationToken ct)=>throw new NotSupportedException();
    }
}
