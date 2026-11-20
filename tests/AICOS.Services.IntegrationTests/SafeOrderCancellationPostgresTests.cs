using System.Net;
using System.Net.Http.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Orders;
using AICOS.KitchenOperations.Api.Domain.Enums;
using AICOS.KitchenOperations.Api.Persistence;
using AICOS.Ordering.Api.Features.OrderCancellation;
using AICOS.Ordering.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AICOS.Services.IntegrationTests;

// Real PostgreSQL advisory locks + HTTP TestServer handlers; Identity/JWT and transport fault injection are test doubles.
[Collection("Session bill PostgreSQL")]
public sealed class SafeOrderCancellationPostgresTests : IAsyncLifetime
{
    private readonly SessionBillHarness _h = new() { EnableSafeCancellation = true, DiscountActor = new DiscountActorDouble() };
    private readonly FaultClient _fault;
    private Guid _order;
    public SafeOrderCancellationPostgresTests() { _fault = new(_h); _h.CancellationClient = _fault; }
    public async Task InitializeAsync()
    {
        await _h.InitializeAsync(Environment.GetEnvironmentVariable("AICOS_BILL_TEST_CONNECTION")!);
        _order = await _h.RoundAsync(); await _h.RoundAsync();
    }
    public Task DisposeAsync() { _h.Dispose(); return Task.CompletedTask; }
    private async Task<OrderCancellationOperation> Operation()
    {
        using var scope = _h.Ordering.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<OrderingDbContext>().Cancellations.AsNoTracking().SingleAsync(x => x.OrderId == _order);
    }
    private async Task Dispatch(Guid id)
    {
        using var scope = _h.Ordering.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<OrderCancellationDispatcher>().TryAsync(id, default);
    }
    [BillPostgresFact]
    public async Task Lost_commit_ack_survives_new_dispatcher_and_replay_without_duplicate_ticket()
    {
        _fault.Lose = "commit";
        var response = await SafeOrderCancellationTests.Cancel(_h, _order, (await SafeOrderCancellationTests.Read(_h, _order))!);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var op = await Operation(); op.State.Should().Be("APPLIED");
        await Task.WhenAll(Dispatch(op.Id), Dispatch(op.Id));
        (await Operation()).State.Should().Be("COMPLETED");
        using var scope = _h.Kitchen.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<KitchenOperationsDbContext>();
        (await db.Tickets.CountAsync(x => x.OrderId == _order)).Should().Be(1);
        (await db.CancellationReceipts.CountAsync(x => x.OrderId == _order)).Should().Be(1);
        (await db.Tickets.SingleAsync(x => x.OrderId == _order)).Status.Should().Be(KitchenTicketStatus.Cancelled);
        (await _h.BillAsync()).Quote.Total.Should().Be(50000);
    }
    [BillPostgresFact]
    public async Task Lost_reserve_ack_keeps_kernel_guard_and_resumes_with_same_receipt()
    {
        _fault.Lose = "reserve";
        (await SafeOrderCancellationTests.Cancel(_h, _order, (await SafeOrderCancellationTests.Read(_h, _order))!)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        var op = await Operation(); op.State.Should().Be("QUEUED");
        using var scope = _h.Kitchen.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<KitchenOperationsDbContext>();
        var ticket = await db.Tickets.SingleAsync(x => x.OrderId == _order);
        using var bar = _h.Kitchen.As(AicosRoles.Barista, _h.Branch);
        await bar.PutAsJsonAsync($"/api/v1/kitchen/tickets/{ticket.Id}/status", new { status = "Brewing" }).ShouldBe(409, "order.cancel_pending");
        await Dispatch(op.Id);
        (await Operation()).State.Should().Be("COMPLETED");
    }
    [BillPostgresFact]
    public async Task Two_different_requests_cannot_cancel_twice()
    {
        var view = (await SafeOrderCancellationTests.Read(_h, _order))!;
        var results = await Task.WhenAll(SafeOrderCancellationTests.Cancel(_h, _order, view, "parallel-cancel-a"), SafeOrderCancellationTests.Cancel(_h, _order, view, "parallel-cancel-b"));
        results.Count(x => x.IsSuccessStatusCode).Should().Be(1);
        results.Count(x => x.StatusCode == HttpStatusCode.Conflict).Should().Be(1);
        using var scope = _h.Ordering.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<OrderingDbContext>().Cancellations.CountAsync(x => x.OrderId == _order)).Should().Be(1);
    }
    [BillPostgresFact]
    public async Task Cancellation_and_cash_are_serialized_without_changing_a_paid_bill()
    {
        var view = (await SafeOrderCancellationTests.Read(_h, _order))!; var bill = await _h.BillAsync();
        var replies = await Task.WhenAll(SafeOrderCancellationTests.Cancel(_h, _order, view), _h.CollectAsync(bill, "cash"));
        replies[0].IsSuccessStatusCode.Should().NotBe(replies[1].IsSuccessStatusCode);
        var after = await _h.BillAsync();
        if (replies[1].IsSuccessStatusCode) { after.Quote.Total.Should().Be(100000); after.Status.Should().Be("PAID"); }
        else { after.Quote.Total.Should().Be(50000); after.Status.Should().Be("DRAFT"); }
    }
    [BillPostgresFact]
    public async Task Bar_accept_and_cancel_have_only_one_winner()
    {
        using var scope = _h.Kitchen.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<KitchenOperationsDbContext>();
        var ticket = await db.Tickets.SingleAsync(x => x.OrderId == _order);
        var view = (await SafeOrderCancellationTests.Read(_h, _order))!;
        using var bar = _h.Kitchen.As(AicosRoles.Barista, _h.Branch);
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/kitchen/barista/tickets/{ticket.Id}/accept") { Content = JsonContent.Create(new { expectedStatus = "Pending", expectedRemakeCount = 0 }) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var replies = await Task.WhenAll(SafeOrderCancellationTests.Cancel(_h, _order, view), bar.SendAsync(request));
        replies[0].EnsureSuccessStatusCode();
        var op = await Operation();
        op.State.Should().BeOneOf("COMPLETED", "REJECTED");
        if (op.State == "COMPLETED") { replies[1].StatusCode.Should().Be(HttpStatusCode.Conflict); }
        else replies[1].EnsureSuccessStatusCode();
    }
    [BillPostgresFact]
    public async Task Nonempty_audit_and_receipts_prevent_destructive_rollback()
    {
        (await SafeOrderCancellationTests.Cancel(_h, _order, (await SafeOrderCancellationTests.Read(_h, _order))!)).EnsureSuccessStatusCode();
        using var os = _h.Ordering.Services.CreateScope(); var ordering = os.ServiceProvider.GetRequiredService<OrderingDbContext>();
        await FluentActions.Awaiting(() => ordering.GetService<IMigrator>().MigrateAsync("20261118143317_OrderingSessionManualDiscount")).Should().ThrowAsync<PostgresException>();
        using var ks = _h.Kitchen.Services.CreateScope(); var kitchen = ks.ServiceProvider.GetRequiredService<KitchenOperationsDbContext>();
        var previous = kitchen.Database.GetMigrations().Reverse().Skip(1).First();
        await FluentActions.Awaiting(() => kitchen.GetService<IMigrator>().MigrateAsync(previous)).Should().ThrowAsync<PostgresException>();
        (await Operation()).State.Should().Be("COMPLETED");
    }

    private sealed class FaultClient(SessionBillHarness h) : IKitchenCancellationClient
    {
        public string? Lose;
        public async Task<CancellationAck> SendAsync(string action, CancellationReservation r, CancellationToken ct)
        {
            using var client = h.Kitchen.CreateClient(); client.DefaultRequestHeaders.Add(OrderCancellationMachineAuth.Header, h.CancellationKey);
            var response = await client.PostAsJsonAsync("/internal/order-cancellations/" + action, r, ct); response.EnsureSuccessStatusCode();
            if (Lose == action) { Lose = null; throw new HttpRequestException("Test lost ACK after Kitchen commit"); }
            return (await response.Content.ReadFromJsonAsync<CancellationAck>(cancellationToken: ct))!;
        }
    }
}
