using System.Net.Http.Json;
using System.Text.Json;
using AICOS.Payments.Api.Features.Payments;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
namespace AICOS.Services.IntegrationTests;

public sealed class TakeawayQrTests : PaymentTestBase
{
    public TakeawayQrTests() : base(s => {
        s.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
            ["TakeawayQr:Enabled"]="true", ["TakeawayQr:AllowTestProvider"]="true", ["CashierShifts:Enabled"]="true"
        }).Build());
    }) { }
    private string Path(Guid id) => $"/api/v1/payments/staff/takeaway/{id}/qr";
    private async Task<HttpResponseMessage> Create(Guid id, string key, decimal amount = 50000, HttpClient? who = null)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, Path(id)) {Content=JsonContent.Create(new { branchId=B1,amount })};
        message.Headers.Add("Idempotency-Key", key);
        return await (who ?? Cashier1).SendAsync(message);
    }
    [Fact]
    public async Task Same_key_replays_after_success_and_drift_is_rejected()
    {
        var id=Guid.NewGuid();Orders.Set(id,B1,50000);var key=Guid.NewGuid().ToString("N");
        var first=await (await Create(id,key)).Body();
        var attempt=first.GetProperty("attempt");
        var reference=attempt.GetProperty("transactionRef").GetString()!;
        (await Create(id,key)).StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        await WebhookAsync("Mock","qr-test-event",reference,"success",50000).ShouldBe(200);
        var paid=await (await Create(id,key)).Body();
        paid.GetProperty("state").GetString().Should().Be("Confirmed");
        paid.GetProperty("attempt").Id().Should().Be(attempt.Id());
        await Create(id,key,60000).ShouldBe(409,"payment.request_key_conflict");
        await Create(id,Guid.NewGuid().ToString("N")).ShouldBe(409);
    }
    [Fact]
    public async Task Failed_attempt_remains_replayable_and_does_not_release_cash_collection()
    {
        var id=Guid.NewGuid();Orders.Set(id,B1,50000);var key=Guid.NewGuid().ToString("N");
        var first=await (await Create(id,key)).Body();
        await WebhookAsync("Mock","qr-failed",first.GetProperty("attempt").GetProperty("transactionRef").GetString()!,"failed",50000).ShouldBe(200);
        var failed=await (await Create(id,key)).Body();
        failed.GetProperty("state").GetString().Should().Be("Failed");
        failed.GetProperty("canCreate").GetBoolean().Should().BeFalse();
        await Cashier1.Post(Tx+"/cash",new {orderId=id,branchId=B1,amount=50000}).ShouldBe(409,"payment.online_pending");
    }
    [Fact]
    public async Task Disabled_flag_blocks_create_and_public_description_cannot_forge_an_intent()
    {
        var id=Guid.NewGuid();Orders.Set(id,B1,50000);
        await Customer.Post(Tx,new {orderId=id,branchId=B1,amount=50000,providerCode="Mock",description="  "+TakeawayQrEndpoints.Prefix+"forged"}).ShouldBe(400);
        F.Services.GetRequiredService<IConfiguration>()["TakeawayQr:Enabled"]="false";
        var state=await GetAsync(Cashier1,Path(id)+"?branchId="+B1);
        state.GetProperty("available").GetBoolean().Should().BeFalse();
        await Create(id,Guid.NewGuid().ToString("N")).ShouldBe(409,"payment.takeaway_qr_disabled");
    }
    [Fact]
    public async Task Read_is_scoped_and_has_no_expiry_or_new_attempt_side_effect()
    {
        var id=Guid.NewGuid();Orders.Set(id,B1,50000);
        await Customer.GetAsync(Path(id)+"?branchId="+B1).ShouldBe(403);
        await Cashier2.GetAsync(Path(id)+"?branchId="+B1).ShouldBe(403);
        var empty=await GetAsync(Cashier1,Path(id)+"?branchId="+B1);
        empty.GetProperty("canCreate").GetBoolean().Should().BeTrue();
        var key=Guid.NewGuid().ToString("N");await Create(id,key).ShouldBe(200);

        var read=await GetAsync(Cashier1,Path(id)+"?branchId="+B1);
        read.GetProperty("attempt").GetProperty("status").GetString().Should().Be("Pending");
        read.GetProperty("qrPayload").GetString().Should().StartWith("aicos-mock://");
        F.Clock.Now = F.Clock.Now.AddHours(2);
        var expiredView=await GetAsync(Cashier1,Path(id)+"?branchId="+B1);
        expiredView.GetProperty("state").GetString().Should().Be("Unknown");
        expiredView.GetProperty("qrPayload").ValueKind.Should().Be(JsonValueKind.Null);
        expiredView.GetProperty("attempt").GetProperty("status").GetString().Should().Be("Pending");
        expiredView.GetProperty("canCreate").GetBoolean().Should().BeFalse();
    }
}
