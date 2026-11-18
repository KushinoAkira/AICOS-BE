using System.Net;
using System.Net.Http.Json;
using AICOS.Ordering.Api.Features.SessionBilling;
using AICOS.Ordering.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AICOS.Services.IntegrationTests;

/// <summary>Real PostgreSQL locking/transactions over HTTP TestServer; Identity/Kitchen/Payment transports remain explicit doubles.</summary>
[Collection("Session bill PostgreSQL")]
public sealed class SessionDiscountPostgresTests : IAsyncLifetime
{
    private readonly SessionBillHarness _h = new() { EnableSessionDiscount = true, DiscountActor = new DiscountActorDouble() };
    private string Path => $"/api/v1/orders/qr/sessions/{_h.Session}/manual-discount";
    public async Task InitializeAsync()
    {
        await _h.InitializeAsync(Environment.GetEnvironmentVariable("AICOS_BILL_TEST_CONNECTION")!);
        await _h.RoundAsync(); await _h.RoundAsync();
    }
    public Task DisposeAsync() { _h.Dispose(); return Task.CompletedTask; }

    [BillPostgresFact]
    public async Task A_new_round_blocks_cash_until_the_discount_is_reviewed_on_postgresql()
    {
        var before = (await _h.OrderCashier.GetFromJsonAsync<SessionDiscountView>(Path))!;
        (await SessionDiscountTests.Set(_h.OrderCashier, _h.Session, before, 10, "PERCENT")).EnsureSuccessStatusCode();
        await _h.RoundAsync();
        var changed = (await _h.OrderCashier.GetFromJsonAsync<SessionDiscountView>(Path))!;
        changed.ReviewRequired.Should().BeTrue(); changed.Discount.Should().Be(10000);
        (await _h.BillAsync()).Quote.ManualDiscountReviewRequired.Should().BeTrue();
        await _h.CollectAsync(await _h.BillAsync(), "cash").ShouldBe(409);
        (await SessionDiscountTests.Set(_h.OrderCashier, _h.Session, changed, 10, "PERCENT")).EnsureSuccessStatusCode();
        var fresh = await _h.BillAsync(); fresh.Quote.Total.Should().Be(135000);
        (await _h.CashAsync(fresh)).Payment!.Amount.Should().Be(135000);
    }

    [BillPostgresFact]
    public async Task Rollback_refuses_nonempty_audit_and_preserves_discounted_orders()
    {
        var before = (await _h.OrderCashier.GetFromJsonAsync<SessionDiscountView>(Path))!;
        (await SessionDiscountTests.Set(_h.OrderCashier, _h.Session, before, 10000)).EnsureSuccessStatusCode();
        using var scope = _h.Ordering.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        await FluentActions.Awaiting(() => db.GetService<IMigrator>().MigrateAsync("20261114164841_DurableTableTransfer"))
            .Should().ThrowAsync<PostgresException>();
        (await db.SessionDiscountEvents.CountAsync(x => x.SessionId == _h.Session)).Should().Be(1);
        (await _h.BillAsync()).Quote.Total.Should().Be(90000);
    }

    [BillPostgresFact]
    public async Task Proposed_audit_acl_is_append_only_and_does_not_grant_payment_access()
    {
        var connection = Environment.GetEnvironmentVariable("AICOS_BILL_TEST_CONNECTION")!;
        await SessionBillHarness.GuardAsync(connection);
        await using var db = new NpgsqlConnection(connection); await db.OpenAsync();
        // A new role in this run's scratch cluster only; no operational service role is changed.
        var role = "discount_acl_" + Guid.NewGuid().ToString("N");
        await using (var grant = new NpgsqlCommand($"""
            CREATE ROLE {role} NOLOGIN;
            GRANT USAGE ON SCHEMA ordering TO {role};
            GRANT SELECT ON ALL TABLES IN SCHEMA ordering TO {role};
            GRANT INSERT ON ordering.session_discount_events TO {role};
            GRANT UPDATE(discount_amount,updated_at_utc,updated_by) ON ordering.orders TO {role};
            """, db)) await grant.ExecuteNonQueryAsync();
        await using var check = new NpgsqlCommand($"""
            SELECT has_table_privilege('{role}','ordering.session_discount_events','SELECT'),
              has_table_privilege('{role}','ordering.session_discount_events','INSERT'),
              has_table_privilege('{role}','ordering.session_discount_events','UPDATE'),
              has_table_privilege('{role}','ordering.session_discount_events','DELETE'),
              has_table_privilege('{role}','payment.session_bills','SELECT'),
              has_column_privilege('{role}','ordering.orders','discount_amount','UPDATE');
            """, db);
        await using var reader = await check.ExecuteReaderAsync(); (await reader.ReadAsync()).Should().BeTrue();
        reader.GetBoolean(0).Should().BeTrue(); reader.GetBoolean(1).Should().BeTrue();
        reader.GetBoolean(2).Should().BeFalse(); reader.GetBoolean(3).Should().BeFalse();
        reader.GetBoolean(4).Should().BeFalse(); reader.GetBoolean(5).Should().BeTrue();
    }

    [BillPostgresFact]
    public async Task Concurrent_discounts_use_revision_and_exactly_one_atomic_audit_receipt()
    {
        var view = (await _h.OrderCashier.GetFromJsonAsync<SessionDiscountView>(Path))!;
        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(i =>
            SessionDiscountTests.Set(_h.OrderCashier, _h.Session, view, 10000 + i)));
        responses.Count(x => x.StatusCode == HttpStatusCode.OK).Should().Be(1);
        responses.Count(x => x.StatusCode == HttpStatusCode.Conflict).Should().Be(11);
        using var scope = _h.Ordering.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        var events = await db.SessionDiscountEvents.Where(x => x.SessionId == _h.Session).ToListAsync();
        events.Should().HaveCount(1);
        var orders = await db.Orders.Include(x => x.Items).Where(x => x.TableSessionId == _h.Session).ToListAsync();
        orders.Sum(x => x.DiscountAmount).Should().Be(events.Single().Value);
        var quote = await _h.BillAsync(); quote.Quote.Total.Should().Be(orders.Sum(x => x.FinalAmount));
        var paid = await _h.CashAsync(quote); paid.Payment!.Amount.Should().Be(quote.Quote.Total);
    }

    [BillPostgresFact]
    public async Task Concurrent_replays_do_not_stack_and_failed_stale_attempt_has_no_partial_writes()
    {
        var before = (await _h.OrderCashier.GetFromJsonAsync<SessionDiscountView>(Path))!;
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            SessionDiscountTests.Set(_h.OrderCashier, _h.Session, before, 12345, key: "lost-ack-replay-key")));
        responses.All(x => x.StatusCode == HttpStatusCode.OK).Should().BeTrue();
        var receipts = await Task.WhenAll(responses.Select(x => x.Content.ReadFromJsonAsync<SessionDiscountView>()));
        receipts.Select(x => x!.OperationId).Distinct().Should().HaveCount(1);
        await SessionDiscountTests.Set(_h.OrderCashier, _h.Session, before, 1).ShouldBe(409);
        using var scope = _h.Ordering.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        (await db.SessionDiscountEvents.CountAsync(x => x.SessionId == _h.Session)).Should().Be(1);
        (await db.Orders.Where(x => x.TableSessionId == _h.Session).SumAsync(x => x.DiscountAmount)).Should().Be(12345);
        (await _h.OrderCashier.GetFromJsonAsync<SessionDiscountView>(Path))!.Total.Should().Be(87655);
    }

    [BillPostgresFact]
    public async Task Cash_and_discount_race_cannot_collect_an_obsolete_quote()
    {
        var bill = await _h.BillAsync();
        var before = (await _h.OrderCashier.GetFromJsonAsync<SessionDiscountView>(Path))!;
        var discountTask = SessionDiscountTests.Set(_h.OrderCashier, _h.Session, before, 20000);
        var cashTask = _h.CollectAsync(bill, "cash");
        await Task.WhenAll(discountTask, cashTask);
        var discount = await discountTask; var cash = await cashTask;
        (discount.IsSuccessStatusCode ^ cash.IsSuccessStatusCode).Should().BeTrue();
        if (cash.IsSuccessStatusCode)
        {
            discount.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await _h.BillAsync()).Payment!.Amount.Should().Be(100000);
        }
        else
        {
            cash.StatusCode.Should().Be(HttpStatusCode.Conflict);
            var fresh = await _h.BillAsync(); fresh.Quote.Total.Should().Be(80000);
            (await _h.CashAsync(fresh)).Payment!.Amount.Should().Be(80000);
        }
    }
}
