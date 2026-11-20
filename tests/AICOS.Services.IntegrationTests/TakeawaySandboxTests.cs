using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AICOS.Payments.Api.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AICOS.Services.IntegrationTests;

// HTTP TestServer/InMemory with fake provider transport and Identity/JWT; not external ZaloPay sandbox.
public sealed class TakeawaySandboxTests : PaymentTestBase
{
    private static readonly FakeZaloPayApi Api = new();
    public TakeawaySandboxTests() : base(s =>
    {
        s.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["TakeawayQr:ZaloPaySandboxEnabled"]="true" }).Build());
        s.PostConfigure<ZaloPayOptions>(o => { o.Live = true; o.TestPage = false; o.ApiUrl = "https://sb-openapi.zalopay.vn"; });
        s.AddHttpClient(ZaloPayGateway.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Api);
    }, "Development")
    {
        Api.Calls.Clear(); Api.Answer = c => c.Path == "/v2/create"
            ? new { return_code = 1, order_url = "https://qcgateway.zalopay.vn/openinapp?order=" + c.Form["app_trans_id"] }
            : new { return_code = 1, amount = 50000, zp_trans_id = 123 };
        Super.PostOk(Providers, new { code="ZaloPay", displayName="Sandbox test", mode="Sandbox", merchantId="2553", apiKey="test-key-one", secretKey="test-key-two" }).GetAwaiter().GetResult();
        SetProviderEnabledAsync("ZaloPay", true).GetAwaiter().GetResult();
        Admin.Put(Merchants, new { providerCode="ZaloPay", isEnabled=true, merchantCode="test-only" }).ShouldBe(201).GetAwaiter().GetResult();
    }
    private string Path(Guid id) => $"/api/v1/payments/staff/takeaway/{id}/online";
    private Task<HttpResponseMessage> Create(Guid id, decimal amount=50000)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Path(id)) { Content=JsonContent.Create(new { branchId=B1,amount }) };
        request.Headers.Add("Idempotency-Key", "stable-sandbox-test-key"); return Cashier1.SendAsync(request);
    }
    [Fact]
    public async Task Create_replay_query_and_reopen_reuse_the_same_order_payment()
    {
        var id=Guid.NewGuid(); Orders.Set(id,B1,50000);
        var first=await (await Create(id)).Body(); first.GetProperty("version").GetInt32().Should().Be(2);
        first.GetProperty("state").GetString().Should().Be("Pending");
        var payment=first.GetProperty("attempt").Id();
        (await Create(id)).EnsureSuccessStatusCode(); Api.Calls.Count(c=>c.Path=="/v2/create").Should().Be(1);
        var paid=await (await Cashier1.PostAsJsonAsync(Path(id)+$"/{payment}/query?branchId={B1}",new {})).Body();
        paid.GetProperty("state").GetString().Should().Be("Confirmed");
        var reopened=await GetAsync(Cashier1,Path(id)+"?branchId="+B1);
        reopened.GetProperty("attempt").Id().Should().Be(payment);
        (await Create(id)).EnsureSuccessStatusCode(); Api.Calls.Count(c=>c.Path=="/v2/create").Should().Be(1);
    }
    [Fact]
    public async Task Disabled_flag_and_cross_branch_block_creation_without_provider_calls()
    {
        var id=Guid.NewGuid();Orders.Set(id,B1,50000);
        F.Services.GetRequiredService<IConfiguration>()["TakeawayQr:ZaloPaySandboxEnabled"]="false";
        await Create(id).ShouldBe(409,"payment.sandbox_unavailable");
        (await Cashier2.GetAsync(Path(id)+"?branchId="+B1)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        Api.Calls.Should().BeEmpty();
    }
    [Fact]
    public async Task Wrong_amount_and_existing_cash_do_not_create_another_obligation()
    {
        var id=Guid.NewGuid();Orders.Set(id,B1,50000);
        await Create(id,49999).ShouldBe(400);
        (await Cashier1.Post(Tx+"/cash",new {orderId=id,branchId=B1,amount=50000})).EnsureSuccessStatusCode();
        await Create(id).ShouldBe(409); Api.Calls.Should().BeEmpty();
    }
    [Theory]
    [InlineData("https://openapi.zalopay.vn",false)]
    [InlineData("https://sb-openapi.zalopay.vn",true)]
    public async Task Production_host_and_internal_test_page_are_not_accepted_as_sandbox(string url,bool testPage)
    {
        var id=Guid.NewGuid();Orders.Set(id,B1,50000);
        var options=F.Services.GetRequiredService<IOptions<ZaloPayOptions>>().Value;options.ApiUrl=url;options.TestPage=testPage;
        await Create(id).ShouldBe(409,"payment.sandbox_unavailable");Api.Calls.Should().BeEmpty();
    }
    [Fact]
    public async Task Provider_failure_does_not_allow_a_new_attempt_by_reposting()
    {
        var id=Guid.NewGuid();Orders.Set(id,B1,50000);
        var initial=await (await Create(id)).Body();var payment=initial.GetProperty("attempt").Id();
        Api.Answer=_=>new{return_code=2,return_message="Test provider declined"};
        (await Cashier1.PostAsJsonAsync(Path(id)+$"/{payment}/query?branchId={B1}",new {})).EnsureSuccessStatusCode();
        var replay=await (await Create(id)).Body();replay.GetProperty("attempt").Id().Should().Be(payment);
        replay.GetProperty("canCreate").GetBoolean().Should().BeFalse();Api.Calls.Count(c=>c.Path=="/v2/create").Should().Be(1);
    }
}
