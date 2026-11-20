using System.Net;
using System.Net.Http.Json;
using AICOS.Payments.Api.Features.SessionBilling;
using AICOS.Payments.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AICOS.Services.IntegrationTests;

[Collection("Session bill PostgreSQL")]
public sealed class PayOsBillPostgresTests : IAsyncLifetime
{
    private readonly PayOsBillFixture _f = new();
    public Task InitializeAsync() => _f.InitializeAsync(Environment.GetEnvironmentVariable("AICOS_BILL_TEST_CONNECTION")!);
    public Task DisposeAsync() { _f.Dispose(); return Task.CompletedTask; }

    [BillPostgresFact]
    public async Task Concurrent_bank_attempts_and_signed_receipts_have_one_intent_payment_and_collection()
    {
        var draft = await _f.H.BillAsync();
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => _f.CreateAsync(draft)));
        foreach (var response in responses) response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync());
        _f.Api.Calls.Count(x => x.Method == HttpMethod.Post).Should().Be(1);
        var pending = await _f.H.BillAsync();
        var hooks = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => _f.HookAsync(_f.Receipt(pending))));
        foreach (var response in hooks) response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync());
        (await _f.H.BillAsync()).Status.Should().Be("PAID");
        using var scope = _f.H.Payments.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        (await db.BillAttempts.CountAsync(x => x.BillId == draft.Id)).Should().Be(1);
        (await db.BillJournal.CountAsync(x => x.BillId == draft.Id && x.Kind == "PAID")).Should().Be(1);
        (await db.Collections.CountAsync(x => x.Id == pending.Payment!.Id)).Should().Be(1);
    }

    [BillPostgresFact]
    public async Task Provider_ack_loss_and_payment_service_restart_query_original_durable_attempt()
    {
        _f.LoseCreateAck = true; var draft = await _f.H.BillAsync(); await _f.CreateAsync(draft).ShouldBe(503);
        var pending = await _f.H.BillAsync(); var original = pending.Payment!.Id;
        await _f.H.RestartPaymentsAsync();
        (await _f.H.BillAsync()).Payment!.Id.Should().Be(original);
        (await _f.CreateAsync(draft)).EnsureSuccessStatusCode();
        _f.Api.Calls.Count(x => x.Method == HttpMethod.Post).Should().Be(1);
        _f.QueryState = "PAID"; (await _f.QueryAsync(pending)).EnsureSuccessStatusCode();
        var paid = await _f.H.BillAsync(); paid.Status.Should().Be("PAID"); paid.Payment!.Id.Should().Be(original);
    }
}
