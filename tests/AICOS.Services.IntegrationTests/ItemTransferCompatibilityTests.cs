using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Billing;
using AICOS.Payments.Api.Features.SessionBilling;
using AICOS.Ordering.Api.Features.SessionBilling;
using AICOS.Ordering.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AICOS.Services.IntegrationTests;

/// <summary>Compatibility probes of current handlers; EF InMemory/test JWT, not transferred-payment E2E.</summary>
public sealed class ItemTransferCompatibilityTests
{
    private static async Task<BillView> ThreeCupsAsync(SessionBillHarness h)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
        {
            Content = JsonContent.Create(new
            {
                branchId = h.Branch,
                type = "DineIn",
                tableId = h.Table,
                tableNumber = "A01",
                tableQrToken = h.Qr,
                expectedSessionId = h.Session,
                items = new[] { new { productId = h.Product, quantity = 3 } }
            })
        };
        request.Headers.Add("Idempotency-Key", "item-design-three-cups");
        (await h.OrderCashier.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Created);
        return await h.BillAsync();
    }

    [Fact]
    public async Task Current_quote_owns_whole_original_order_and_cannot_freeze_a_partial_unit_fingerprint()
    {
        using var h = new SessionBillHarness();
        await h.InitializeAsync();
        var bill = await ThreeCupsAsync(h);
        bill.Quote.Lines.Single().Quantity.Should().Be(3);
        bill.Quote.Total.Should().Be(150000);
        bill.Quote.Orders.Single().Amount.Should().Be(150000);
        using var client = h.Ordering.CreateClient();
        client.DefaultRequestHeaders.Add(BillingMachineAuth.Header, h.MachineKey);
        var proposedUnitFingerprint = BillIdentity.Hash("one-cup-unit-manifest");
        var response = await client.PostAsJsonAsync($"/internal/session-billing/sessions/{h.Session}/freeze",
            new FreezeBillRequest(h.Tenant, h.Branch, bill.Id, proposedUnitFingerprint));
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await h.BillAsync()).Fingerprint.Should().Be(bill.Fingerprint);
        using var scope = h.Ordering.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<OrderingDbContext>().SessionCheckouts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Current_paid_receiver_rejects_partial_obligation_without_marking_original_order_paid()
    {
        using var h = new SessionBillHarness();
        await h.InitializeAsync();
        var bill = await ThreeCupsAsync(h);
        var line = bill.Quote.Lines.Single();
        var partial = bill.Quote with
        {
            Fingerprint = BillIdentity.Hash("partial-unit-design"),
            Subtotal = 50000,
            Total = 50000,
            Orders = [bill.Quote.Orders.Single() with { Amount = 50000 }],
            Lines = [line with { Quantity = 1, Total = 50000 }]
        };
        // Synthetic proposed V2 fence tests the current receiver's contract boundary; not a real collection.
        using (var scope = h.Ordering.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            db.SessionCheckouts.Add(new SessionCheckout
            {
                SessionId = h.Session,
                TenantId = h.Tenant,
                BranchId = h.Branch,
                BillId = bill.Id,
                Fingerprint = partial.Fingerprint,
                QuoteJson = JsonSerializer.Serialize(partial),
                CreatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
        using var machine = h.Ordering.CreateClient();
        machine.DefaultRequestHeaders.Add(BillingMachineAuth.Header, h.MachineKey);
        var response = await machine.PostAsJsonAsync("/internal/session-billing/paid",
            new BillPaidNotice(h.Tenant, h.Branch, h.Session, bill.Id, partial.Fingerprint, Guid.NewGuid(), "Cash", DateTime.UtcNow));
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var verify = h.Ordering.Services.CreateScope();
        var orders = verify.ServiceProvider.GetRequiredService<OrderingDbContext>();
        (await orders.Orders.SingleAsync()).IsPaid.Should().BeFalse();
        (await orders.Orders.SingleAsync()).TableSessionId.Should().Be(h.Session);
        (await orders.SessionCheckouts.SingleAsync()).PaymentId.Should().BeNull();
    }
}
