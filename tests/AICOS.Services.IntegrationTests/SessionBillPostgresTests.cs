using System.Net;
using System.Net.Http.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Payments.Api.Persistence;
using AICOS.Ordering.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
namespace AICOS.Services.IntegrationTests;
public sealed class BillPostgresFactAttribute : FactAttribute
{ public BillPostgresFactAttribute() { if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AICOS_BILL_TEST_CONNECTION"))) Skip = "Run scripts/Test-SessionBillingPostgres.ps1 -Run (isolated cluster only)."; } }
[CollectionDefinition("Session bill PostgreSQL", DisableParallelization = true)] public sealed class BillPostgresCollection { }
[Collection("Session bill PostgreSQL")]
public sealed class SessionBillPostgresTests : IAsyncLifetime
{
    private readonly SessionBillHarness _h = new();
    public Task InitializeAsync() => _h.InitializeAsync(Environment.GetEnvironmentVariable("AICOS_BILL_TEST_CONNECTION")!);
    public Task DisposeAsync() { _h.Dispose(); return Task.CompletedTask; }
    [BillPostgresFact]
    public async Task Sixteen_cashiers_cannot_collect_a_bill_twice()
    {
        await _h.RoundAsync(); await _h.RoundAsync(); var draft = await _h.BillAsync();
        var responses = await Task.WhenAll(Enumerable.Range(0, 16).Select(async i =>
        {
            using var cashier = _h.Payments.As(AicosRoles.Cashier, _h.Branch, Guid.NewGuid());
            return await _h.CollectAsync(draft, "cash", "cash-parallel-" + i, caller: cashier);
        }));
        responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1); responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).Should().Be(15);
        using var s = _h.Payments.Services.CreateScope(); var db = s.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        (await db.BillAttempts.CountAsync(a => a.BillId == draft.Id)).Should().Be(1); (await db.SessionBillOrders.CountAsync(a => a.BillId == draft.Id)).Should().Be(2);
    }
    [BillPostgresFact]
    public async Task Add_order_and_cash_have_one_linearized_snapshot()
    {
        await _h.RoundAsync(); var draft = await _h.BillAsync();
        var add = _h.AddRoundAsync("racing-order-key"); var cash = _h.CollectAsync(draft, "cash", "racing-cash-key"); await Task.WhenAll(add, cash);
        if (cash.Result.IsSuccessStatusCode) { add.Result.StatusCode.Should().Be(HttpStatusCode.Conflict); (await _h.BillAsync()).Quote.Orders.Should().HaveCount(1); }
        else { cash.Result.StatusCode.Should().Be(HttpStatusCode.Conflict); add.Result.IsSuccessStatusCode.Should().BeTrue(); (await _h.BillAsync()).Quote.Orders.Should().HaveCount(2); }
    }
    [BillPostgresFact]
    public async Task Lost_ack_and_parallel_close_preserve_one_payment_and_closed_session()
    {
        var order = await _h.RoundAsync(); var draft = await _h.BillAsync(); _h.LosePaidAck = true; await _h.CashAsync(draft); await _h.DispatchAgainAsync();
        await _h.ServeAsync(order); var path = $"/api/v1/orders/qr/sessions/{_h.Session}/close";
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => _h.OrderCashier.PostAsJsonAsync(path, new { })));
        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.NoContent); (await _h.BillAsync()).Status.Should().Be("CLOSED");
    }
    [BillPostgresFact]
    public async Task Pending_qr_survives_host_restart_and_concurrent_signed_receipts_apply_once()
    {
        await _h.RoundAsync(); var draft = await _h.BillAsync();
        var response = await _h.CollectAsync(draft, "qr", "restart-qr-key"); response.EnsureSuccessStatusCode();
        var qr = (await response.Content.ReadFromJsonAsync<AICOS.Payments.Api.Features.SessionBilling.BillView>())!;
        await _h.RestartPaymentsAsync(); (await _h.BillAsync()).Payment!.Id.Should().Be(qr.Payment!.Id);
        var receipts = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => _h.ReceiptAsync(qr, "one-receipt-event")));
        receipts.Should().OnlyContain(r => r.IsSuccessStatusCode);
        var paid = await _h.BillAsync(); paid.Status.Should().Be("PAID");
        using var scope = _h.Payments.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        (await db.BillAttempts.CountAsync(a => a.BillId == draft.Id)).Should().Be(1);
        (await db.BillJournal.CountAsync(e => e.BillId == draft.Id && e.Kind == "PAID")).Should().Be(1);
        (await db.BillJournal.CountAsync(e => e.BillId == draft.Id && e.Kind == "PROVIDER_RECEIPT")).Should().Be(1);
    }
    [BillPostgresFact]
    public async Task Outbox_recovers_after_payment_host_restart_without_another_charge()
    {
        await _h.RoundAsync(); _h.KitchenOffline = true; var paid = await _h.CashAsync(await _h.BillAsync(), "restart-cash-key");
        paid.OrderingAcknowledged.Should().BeFalse(); await _h.RestartPaymentsAsync(); _h.KitchenOffline = false;
        await _h.DispatchAgainAsync(); var current = await _h.BillAsync(); current.OrderingAcknowledged.Should().BeTrue();
        current.Payment!.Id.Should().Be(paid.Payment!.Id);
    }
    [BillPostgresFact]
    public async Task Additive_migrations_can_rollback_empty_new_tables_without_losing_legacy_session()
    {
        await _h.RoundAsync();
        using (var s = _h.Payments.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<PaymentsDbContext>(); var applied = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
            (await db.SessionBills.CountAsync()).Should().Be(0, "rollback runs separately before financial fixtures");
            await db.GetService<IMigrator>().MigrateAsync(applied[^2]); await db.Database.MigrateAsync();
        }
        using (var s = _h.Ordering.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<OrderingDbContext>(); var applied = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
            await db.GetService<IMigrator>().MigrateAsync(applied[^2]);
            (await db.TableSessions.CountAsync(x => x.Id == _h.Session)).Should().Be(1);
            await db.Database.MigrateAsync();
        }
    }
}
