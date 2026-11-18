using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AICOS.KitchenOperations.Api.Features.Barista;
using Microsoft.Extensions.Configuration;

namespace AICOS.Services.IntegrationTests;

public sealed class BarPrintAgentTests
{
    private const string Key="isolated-printer-contract-test-key-0000000000";
    private sealed class Agent(Func<HttpRequestMessage,Task<HttpResponseMessage>> respond):HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage message,CancellationToken ct){Calls++;return respond(message);}
    }
    private static KitchenPrintJob Job()=>new(){Id=Guid.NewGuid(),PayloadHash=new string('A',64),Payload=JsonSerializer.Serialize(new BarPrintDocument("AICOS Coffee","SLIP","BAR-TEST","Takeaway",DateTime.UtcNow,DateTime.UtcNow,"Barista",80,30,[new(Guid.NewGuid(),"Latte","Size L",2)]))};
    private static IConfiguration Settings(bool allowTest=false,string database="aicos_billstack_kitchen")=>new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{
        ["BaristaWorkflow:PrintAgentUrl"]="http://printer.test/",["BaristaWorkflow:PrintAgentKey"]=Key,
        ["BaristaWorkflow:AllowTestPrinter"]=allowTest.ToString(),["ASPNETCORE_ENVIRONMENT"]="Development",["ConnectionStrings:Default"]="Host=localhost;Database="+database
    }).Build();
    [Theory]
    [InlineData("PRINTED",true,false,"aicos_billstack_kitchen","PRINTED",false)]
    [InlineData("PRINTED",false,false,"aicos_billstack_kitchen","UNKNOWN",false)]
    [InlineData("TEST_PRINTED",true,false,"aicos_billstack_kitchen","UNKNOWN",false)]
    [InlineData("TEST_PRINTED",true,true,"operating_database","UNKNOWN",false)]
    [InlineData("TEST_PRINTED",true,true,"aicos_billstack_kitchen","PRINTED",true)]
    public async Task Only_matching_signed_ack_counts_as_printed_and_test_output_is_explicit(string status,bool signed,bool test,string database,string expected,bool isTest)
    {
        var job=Job();var handler=new Agent(async message=>{
            message.Headers.GetValues("Idempotency-Key").Single().Should().Be(job.Id.ToString());
            message.Headers.GetValues("X-Aicos-Print-Signature").Single().Should().Be(VerifiedPrintAgent.Sign(Key,job.Id,job.PayloadHash,"REQUEST"));
            var body=await message.Content!.ReadFromJsonAsync<JsonElement>();body.GetProperty("html").GetString().Should().NotContain("35000");
            return new(HttpStatusCode.OK){Content=JsonContent.Create(new{jobId=job.Id,status,signature=signed?VerifiedPrintAgent.Sign(Key,job.Id,job.PayloadHash,status):"invalid"})};
        });
        using var http=new HttpClient(handler);var result=await new VerifiedPrintAgent(http,Settings(test,database)).PrintAsync(job,default);
        result.Status.Should().Be(expected);result.IsTest.Should().Be(isTest);handler.Calls.Should().Be(1);
    }
    [Fact] public async Task Network_timeout_is_unknown_and_does_not_retry_POST()
    {
        var handler=new Agent(_=>throw new TaskCanceledException("synthetic timeout"));using var http=new HttpClient(handler);
        (await new VerifiedPrintAgent(http,Settings()).PrintAsync(Job(),default)).Status.Should().Be("UNKNOWN");handler.Calls.Should().Be(1);
    }
    [Fact] public async Task Missing_printer_configuration_is_failed_without_network_request()
    {
        var handler=new Agent(_=>throw new InvalidOperationException("Must not send"));using var http=new HttpClient(handler);
        var config=new ConfigurationBuilder().Build();var result=await new VerifiedPrintAgent(http,config).PrintAsync(Job(),default);
        result.Status.Should().Be("FAILED");result.ErrorCode.Should().Be("printer_not_configured");handler.Calls.Should().Be(0);
    }
}
