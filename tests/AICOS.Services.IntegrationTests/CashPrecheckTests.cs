using Microsoft.Extensions.DependencyInjection;
﻿using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Payments.Api.Features.CashierShifts;
using FluentAssertions;
namespace AICOS.Services.IntegrationTests;
public sealed class CashPrecheckTests
{
    [Theory]
    [InlineData(false,false,true,"NOT_REQUIRED")]
    [InlineData(true,false,true,"NOT_REQUIRED")]
    [InlineData(false,true,false,"UNAVAILABLE")]
    [InlineData(true,true,false,"MISSING")]
    public async Task Policy_is_read_from_backend_even_when_shifts_are_disabled(bool enabled,bool required,bool allowed,string status)
    {
        using var h=new SessionBillHarness{EnableCashierShifts=enabled,RequireCashierShift=required};await h.InitializeAsync();
        var result=await h.Cashier.GetFromJsonAsync<JsonElement>("/api/v1/payments/cashier-shifts/cash-precheck?branchId="+h.Branch);
        result.GetProperty("allowed").GetBoolean().Should().Be(allowed);result.GetProperty("requiresOpenShift").GetBoolean().Should().Be(required);
        result.GetProperty("shiftStatus").GetString().Should().Be(status);result.GetProperty("cashierId").GetGuid().Should().Be(h.CashierId);
        using var bar=h.Payments.As(AicosRoles.Barista,h.Branch);(await bar.GetAsync("/api/v1/payments/cashier-shifts/cash-precheck?branchId="+h.Branch)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await h.Cashier.GetAsync("/api/v1/payments/cashier-shifts/cash-precheck?branchId="+Guid.NewGuid())).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
    [Fact] public async Task Close_after_allowed_precheck_still_blocks_cash()
    {
        using var h=new SessionBillHarness{EnableCashierShifts=true,RequireCashierShift=true};await h.InitializeAsync();
        using var open=new HttpRequestMessage(HttpMethod.Post,"/api/v1/payments/cashier-shifts"){Content=JsonContent.Create(new{branchId=h.Branch,openingCash=0})};open.Headers.Add("Idempotency-Key","precheck-open-test");
        var shift=await (await h.Cashier.SendAsync(open)).Content.ReadFromJsonAsync<JsonElement>();
        (await h.Cashier.GetFromJsonAsync<JsonElement>("/api/v1/payments/cashier-shifts/cash-precheck?branchId="+h.Branch)).GetProperty("allowed").GetBoolean().Should().BeTrue();
        (await h.Cashier.PostAsJsonAsync("/api/v1/payments/cashier-shifts/"+shift.GetProperty("id").GetGuid()+"/close",new{version=1,countedCash=0})).EnsureSuccessStatusCode();
        await h.RoundAsync();var b=await h.BillAsync();using var cash=new HttpRequestMessage(HttpMethod.Post,"/api/v1/payments/bills/"+b.Id+"/payments/cash"){Content=JsonContent.Create(new{sessionId=h.Session,fingerprint=b.Fingerprint,amountTendered=50000,confirmReceived=true})};cash.Headers.Add("Idempotency-Key","closed-after-precheck");
        (await h.Cashier.SendAsync(cash)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await h.Cashier.GetFromJsonAsync<JsonElement>("/api/v1/payments/cashier-shifts/cash-precheck?branchId="+h.Branch)).GetProperty("shiftStatus").GetString().Should().Be("CLOSED");
    }
    [Fact] public async Task Policy_change_after_precheck_is_revalidated_not_cached()
    {
        using var h=new SessionBillHarness{EnableCashierShifts=true};await h.InitializeAsync();await h.RoundAsync();var b=await h.BillAsync();
        (await h.Cashier.GetFromJsonAsync<JsonElement>("/api/v1/payments/cashier-shifts/cash-precheck?branchId="+h.Branch)).GetProperty("allowed").GetBoolean().Should().BeTrue();
        var config=h.Payments.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();config["CashierShifts:RequireOpenForCash"]="true";
        using var request=new HttpRequestMessage(HttpMethod.Post,"/api/v1/payments/bills/"+b.Id+"/payments/cash"){Content=JsonContent.Create(new{sessionId=h.Session,fingerprint=b.Fingerprint,amountTendered=50000,confirmReceived=true})};request.Headers.Add("Idempotency-Key","policy-changed-cash");
        (await h.Cashier.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        config["CashierShifts:RequireOpenForCash"]="false";await h.CashAsync(b,"policy-changed-cash");
    }
    [Theory] [InlineData("active")] [InlineData("role")] [InlineData("branch")] [InlineData("offline")]
    public async Task Current_account_is_checked_instead_of_stale_claims(string scenario)
    {
        var user=new Actor();using var http=new HttpClient(new MeHandler(user,scenario)){BaseAddress=new Uri("http://identity.test")};var verify=new HttpCashActorVerifier(http);
        if(scenario=="active")await verify.EnsureAsync(user,default);
        else await FluentActions.Awaiting(()=>verify.EnsureAsync(user,default)).Should().ThrowAsync<Exception>();
    }
    private sealed class Actor:ICurrentUser
    { public bool IsAuthenticated=>true;public Guid? UserId{get;}=Guid.NewGuid();public Guid? TenantId{get;}=Guid.NewGuid();public Guid? BranchId{get;}=Guid.NewGuid();public string Name=>"Test";public string Role=>"Cashier";public bool IsInRole(string role)=>role==Role; }
    private sealed class MeHandler(Actor actor,string scenario):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>Task.FromResult(new HttpResponseMessage(scenario=="offline"?HttpStatusCode.ServiceUnavailable:HttpStatusCode.OK){Content=JsonContent.Create(new{id=actor.UserId,tenantId=actor.TenantId,branchId=scenario=="branch"?Guid.NewGuid():actor.BranchId,role=scenario=="role"?"Barista":"Cashier",status="Active",mustChangePassword=false})});}
}
