using AICOS.Contracts.Serving;
using AICOS.Contracts.KitchenAdmission;
using AICOS.KitchenOperations.Api.Features.CupServing;
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;
using AICOS.KitchenOperations.Api;
using AICOS.KitchenOperations.Api.Persistence;
using AICOS.KitchenOperations.Api.Features.Barista;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
namespace AICOS.Services.IntegrationTests;

public sealed class BaristaWorkflowTests : IDisposable
{
    private sealed class Printer : IBarPrinter
    {
        public string Outcome = "PRINTED";
        public int Calls;
        public Task<PrinterResult> PrintAsync(KitchenPrintJob job,CancellationToken ct) { Calls++; return Task.FromResult(new PrinterResult(Outcome,Outcome=="PRINTED"?null:"test_offline",true)); }
    }
    private sealed class Location : IBarLocationDirectory { public Task<string?> AreaAsync(Guid branch,string? table,CancellationToken ct)=>Task.FromResult<string?>("Trong nha"); }
    private sealed class OrderTime : IBarOrderDirectory { public Task<DateTime?> CreatedAtAsync(Guid order,Guid branch,CancellationToken ct)=>Task.FromResult<DateTime?>(new DateTime(2026,11,14,2,0,0,DateTimeKind.Utc)); }
    private readonly Printer _printer = new();
    private readonly ServiceFactory<KitchenOperationsService,KitchenOperationsDbContext> _f;
    private readonly HttpClient _barista;
    private readonly Guid _branch;
    private readonly Guid _actor=Guid.NewGuid();
    private const string Root="/api/v1/kitchen/barista";
    public BaristaWorkflowTests()
    {
        _f=new(services=>{services.RemoveAll<IBarOrderDirectory>();services.AddSingleton<IBarOrderDirectory>(new OrderTime());services.RemoveAll<IBarLocationDirectory>();services.AddSingleton<IBarLocationDirectory>(new Location());services.RemoveAll<IBarPrinter>();services.AddSingleton<IBarPrinter>(_printer);},settings:new Dictionary<string,string?>{{"BaristaWorkflow:Enabled","true"},{"BaristaWorkflow:WorkerEnabled","false"}});
        _branch=_f.Branches.Add(ServiceFactory<KitchenOperationsService,KitchenOperationsDbContext>.DefaultTenantId);
        _barista=_f.As(AicosRoles.Barista,_branch,userId:_actor);
    }
    private async Task<Guid> Ticket()
    {
        await _f.HandleAsync(new OrderCreatedIntegrationEvent(ServiceFactory<KitchenOperationsService,KitchenOperationsDbContext>.DefaultTenantId,Guid.NewGuid(),"BAR-"+Guid.NewGuid().ToString("N")[..6],_branch,null,null,null,"T1","DineIn",110000,false,null,[new OrderItemDto(Guid.NewGuid(),"Latte",55000,2,"Size L, sugar 50%, ice, topping")],DateTime.UtcNow));
        var queue=await _barista.GetFromJsonAsync<JsonElement>("/api/v1/kitchen/queue");return queue.EnumerateArray().Last().Id();
    }
    private Task<HttpResponseMessage> Post(string path,object body,string key="barista-test-key-001",HttpClient? client=null)
    {
        var request=new HttpRequestMessage(HttpMethod.Post,Root+path){Content=JsonContent.Create(body)};request.Headers.Add("Idempotency-Key",key);return (client??_barista).SendAsync(request);
    }
    private async Task<JsonElement> Accept(Guid id){var response=await Post($"/tickets/{id}/accept",new{expectedStatus="Pending",expectedRemakeCount=0});response.EnsureSuccessStatusCode();return await response.Content.ReadFromJsonAsync<JsonElement>();}
    private Task<JsonElement> Detail(Guid id)=>_barista.GetFromJsonAsync<JsonElement>($"{Root}/tickets/{id}");
    private async Task Dispatch(Guid id){using var s=_f.Services.CreateScope();await s.ServiceProvider.GetRequiredService<BarPrintDispatcher>().DispatchAsync(id,default);}

    [Fact] public async Task Admission_and_GET_never_create_print_jobs_or_claim_printed()
    {
        var id=await Ticket();await Detail(id);await Detail(id);
        using var s=_f.Services.CreateScope();(await s.ServiceProvider.GetRequiredService<KitchenOperationsDbContext>().PrintJobs.CountAsync()).Should().Be(0);_printer.Calls.Should().Be(0);
        (await Post($"/tickets/{id}/labels",new{items=Array.Empty<object>()})).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }
    [Fact] public async Task Acceptance_creates_one_slip_no_labels_and_timestamps_survive_replay()
    {
        var id=await Ticket();var first=await Accept(id);var before=await Detail(id);var second=await Accept(id);
        first.Id().Should().Be(second.Id());before.GetProperty("acceptance").GetProperty("acceptedAtUtc").GetString().Should().Be((await Detail(id)).GetProperty("acceptance").GetProperty("acceptedAtUtc").GetString());
        var jobs=(await Detail(id)).GetProperty("printJobs");jobs.GetArrayLength().Should().Be(1);jobs[0].GetProperty("kind").GetString().Should().Be("SLIP");jobs[0].GetProperty("status").GetString().Should().Be("QUEUED");
        _printer.Calls.Should().Be(0);await Dispatch(first.Id());_printer.Calls.Should().Be(1);await Dispatch(first.Id());_printer.Calls.Should().Be(1);
    }
    [Fact] public async Task Other_barista_cannot_take_accepted_ticket_and_legacy_cannot_bypass_acceptance()
    {
        var id=await Ticket();await _barista.Put($"/api/v1/kitchen/tickets/{id}/status",new{status="ReadyForPickup"}).ShouldBe(409);
        await Accept(id);using var other=_f.As(AicosRoles.Barista,_branch,userId:Guid.NewGuid());
        (await Post($"/tickets/{id}/accept",new{expectedStatus="Pending",expectedRemakeCount=0},client:other)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        await _barista.Put($"/api/v1/kitchen/tickets/{id}/status",new{status="Completed"}).ShouldBe(409);
    }
    [Fact] public async Task Selected_labels_use_exact_copies_no_price_or_cup_ordinals_and_same_request_deduplicates()
    {
        var id=await Ticket();await Accept(id);var item=(await Detail(id)).GetProperty("items")[0].Id();var body=new{items=new[]{new{itemId=item,copies=3}}};
        var response=await Post($"/tickets/{id}/labels",body,"labels-request-001");response.EnsureSuccessStatusCode();var job=await response.Content.ReadFromJsonAsync<JsonElement>();
        var replay=await Post($"/tickets/{id}/labels",body,"labels-request-001");(await replay.Content.ReadFromJsonAsync<JsonElement>()).Id().Should().Be(job.Id());
        var preview=await _barista.GetFromJsonAsync<JsonElement>($"{Root}/print-jobs/{job.Id()}/preview");var html=preview.GetProperty("html").GetString()!;
        html.Split("<article>").Length.Should().Be(4);html.Should().NotContain("55.000").And.NotContain(">1/2<").And.NotContain("CupIndex");
        (await Post($"/tickets/{id}/labels",new{items=new[]{new{itemId=item,copies=4}}},"labels-request-001")).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }
    [Fact] public async Task Offline_printer_does_not_undo_acceptance_unknown_never_automatically_retries()
    {
        _printer.Outcome="UNKNOWN";var id=await Ticket();var job=await Accept(id);await Dispatch(job.Id());await Dispatch(job.Id());_printer.Calls.Should().Be(1);
        var row=await Detail(id);row.GetProperty("ticket").GetProperty("status").GetString().Should().Be("Brewing");row.GetProperty("printJobs")[0].GetProperty("status").GetString().Should().Be("UNKNOWN");
        (await Post($"/tickets/{id}/reprint-slip",new{reprintOf=job.Id()},"explicit-reprint-001")).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Post($"/tickets/{id}/reprint-slip",new{reprintOf=job.Id(),confirmUnknownRisk=true},"explicit-reprint-002")).IsSuccessStatusCode.Should().BeTrue();
    }
    [Fact] public async Task Ready_records_actor_once_without_marking_served_and_history_summary_are_read_only()
    {
        var id=await Ticket();await Accept(id);
        await _barista.Put($"/api/v1/kitchen/tickets/{id}/status",new{status="ReadyForPickup",expectedStatus="Brewing",expectedRemakeCount=0}).ShouldBe(200);
        var before=(await Detail(id)).GetProperty("acceptance");before.GetProperty("completedBy").GetGuid().Should().NotBeEmpty();
        await _barista.Put($"/api/v1/kitchen/tickets/{id}/status",new{status="ReadyForPickup",expectedStatus="Brewing",expectedRemakeCount=0}).ShouldBe(200);
        (await Detail(id)).GetProperty("acceptance").GetProperty("completedAtUtc").GetString().Should().Be(before.GetProperty("completedAtUtc").GetString());
        (await _barista.GetAsync($"{Root}/summary?branchId={_branch}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _barista.GetAsync($"{Root}/tickets?history=true&branchId={_branch}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
    [Fact] public async Task Cashier_and_other_branch_cannot_accept_or_print()
    {
        var id=await Ticket();using var cashier=_f.As(AicosRoles.Cashier,_branch);using var foreign=_f.As(AicosRoles.Barista,Guid.NewGuid());
        (await Post($"/tickets/{id}/accept",new{expectedStatus="Pending",expectedRemakeCount=0},client:cashier)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Post($"/tickets/{id}/accept",new{expectedStatus="Pending",expectedRemakeCount=0},client:foreign)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact] public async Task Expired_print_lease_becomes_unknown_without_resending_to_printer()
    {
        var id=await Ticket();var job=await Accept(id);
        using(var scope=_f.Services.CreateScope()){
            var db=scope.ServiceProvider.GetRequiredService<KitchenOperationsDbContext>();var row=await db.PrintJobs.SingleAsync(j=>j.Id==job.Id());
            row.Status="PRINTING";row.StartedAtUtc=_f.Clock.Now.UtcDateTime.AddMinutes(-2);row.LeaseUntilUtc=_f.Clock.Now.UtcDateTime.AddMinutes(-1);row.LeaseOwner=Guid.NewGuid();row.Fence=1;await db.SaveChangesAsync();
        }
        await Dispatch(job.Id());await Dispatch(job.Id());_printer.Calls.Should().Be(0);
        (await Detail(id)).GetProperty("printJobs")[0].GetProperty("status").GetString().Should().Be("UNKNOWN");
    }
    private sealed record BarUser(Guid Tenant,Guid Branch,Guid Actor):ICurrentUser
    {
        public bool IsAuthenticated=>true;public Guid? TenantId=>Tenant;public Guid? BranchId=>Branch;public Guid? UserId=>Actor;
        public string? Name=>"Test Barista";public string? Role=>AicosRoles.Barista;public bool IsInRole(string role)=>role==AicosRoles.Barista;
    }
    [Fact] public async Task CupV1_requires_acceptance_and_tracks_each_cup_without_marking_served()
    {
        var tenant=ServiceFactory<KitchenOperationsService,KitchenOperationsDbContext>.DefaultTenantId;var order=Guid.NewGuid();
        using var scope=_f.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<KitchenOperationsDbContext>();
        var manifest=new CupManifest("BAR-CUPS",FulfillmentModes.CupV1,Guid.NewGuid(),Guid.NewGuid(),[new(Guid.NewGuid(),Guid.NewGuid(),"Latte",2,"Size L")]);
        var request=new CupAdmissionRequest(Guid.NewGuid(),tenant,_branch,order,manifest,CupAdmissionProtocol.Hash(tenant,_branch,order,manifest),new("DineIn","Pending",false,"T1",_f.Clock.Now.UtcDateTime,null,null,null));
        await new CupAdmissionWriter(db,_f.Clock).ApplyAsync(request);
        var id=await db.Tickets.Where(t=>t.OrderId==order).Select(t=>t.Id).SingleAsync();
        var cups=await db.CupTasks.Where(c=>c.OrderId==order).OrderBy(c=>c.OrderCupIndex).ToListAsync();
        var config=scope.ServiceProvider.GetRequiredService<IConfiguration>();var core=new CupServingUnitOfWork(db,new BarUser(tenant,_branch,_actor),_f.Clock,TimeSpan.Zero,config);
        var first=new CupCommand(cups[0].Id,"cup-start-before-accept",CupAction.Start,cups[0].Version,cups[0].PreparationCycle);
        await Assert.ThrowsAsync<AICOS.BuildingBlocks.Exceptions.ConflictException>(()=>core.ExecuteAsync(first));
        await Accept(id);
        foreach(var cup in cups){
            var start=await core.ExecuteAsync(new(cup.Id,"start-"+cup.Id,CupAction.Start,0,1));
            var ready=new CupCommand(cup.Id,"ready-"+cup.Id,CupAction.Ready,start.Cup.Version,1);
            var result=await core.ExecuteAsync(ready);result.Cup.ReadyAtUtc.Should().NotBeNull();
            result.Cup.Service.Should().Be(CupService.Available);
            (await core.ExecuteAsync(ready)).Replayed.Should().BeTrue();
        }
        var detail=await Detail(id);detail.GetProperty("ticket").GetProperty("status").GetString().Should().Be("ReadyForPickup");
        detail.GetProperty("acceptance").GetProperty("completedBy").GetGuid().Should().Be(_actor);
        detail.GetProperty("printJobs").GetArrayLength().Should().Be(1);
        (await db.CupOperations.CountAsync()).Should().Be(4);
    }
    public void Dispose()=>_f.Dispose();
}
