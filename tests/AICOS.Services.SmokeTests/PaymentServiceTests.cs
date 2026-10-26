using System.Net;
using System.Net.Http.Json;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.Payments.Api;
using AICOS.Payments.Api.Domain;
using AICOS.Payments.Api.Features.Payments;
using AICOS.Payments.Api.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AICOS.Services.SmokeTests;

public sealed class PaymentServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 24, 10, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task Payment_endpoint_requires_an_access_token()
    {
        await using var factory = new WebApplicationFactory<PaymentsService>();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/api/v1/payments/bills/{Guid.NewGuid()}/payments/cash",
            new CreatePaymentRequest(Guid.NewGuid(), 100m, 0));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Cash_payment_is_confirmed_once_and_marks_bill_paid()
    {
        await using var context = CreateContext();
        var user = new TestCurrentUser(Guid.NewGuid(), Guid.NewGuid());
        var bill = await SeedBillAsync(context, user, null, Guid.NewGuid(), 110000m);
        var service = CreateService(context);
        var request = new CreatePaymentRequest(Guid.NewGuid(), bill.TotalAmount, 0);

        var first = await service.CreateCashAsync(
            bill.Id, request, user, CancellationToken.None);
        var replay = await service.CreateCashAsync(
            bill.Id, request, user, CancellationToken.None);

        first.Status.Should().Be(PaymentStatuses.Succeeded);
        first.Method.Should().Be(PaymentMethods.Cash);
        first.ConfirmedByUserId.Should().Be(user.UserId);
        replay.Should().BeEquivalentTo(first);
        (await context.Payments.CountAsync()).Should().Be(1);
        (await context.PaymentEvents.CountAsync()).Should().Be(1);
        (await context.Bills.FindAsync(bill.Id))!.Status.Should().Be(BillStatuses.Paid);
    }

    [Fact]
    public async Task Qr_stays_pending_until_manual_confirmation_with_evidence()
    {
        await using var context = CreateContext();
        var user = new TestCurrentUser(Guid.NewGuid(), Guid.NewGuid());
        var sessionId = Guid.NewGuid();
        var bill = await SeedBillAsync(context, user, sessionId, null, 110000m);
        var service = CreateService(context);
        var qr = await service.CreateQrAsync(
            bill.Id,
            new CreatePaymentRequest(Guid.NewGuid(), bill.TotalAmount, 0),
            user,
            CancellationToken.None);

        qr.Status.Should().Be(PaymentStatuses.Pending);
        (await service.GetSettlementStatusAsync(sessionId, user, CancellationToken.None))
            .Settled.Should().BeFalse();
        var invalidConfirmation = () => service.ConfirmBankTransferAsync(
            qr.Id,
            new ConfirmBankTransferRequest(Guid.NewGuid(), qr.Version, " ", null),
            user,
            CancellationToken.None);
        await invalidConfirmation.Should().ThrowAsync<ValidationFailedException>();

        var confirmationId = Guid.NewGuid();
        var confirmed = await service.ConfirmBankTransferAsync(
            qr.Id,
            new ConfirmBankTransferRequest(
                confirmationId,
                qr.Version,
                "BANK-TRACE-881",
                "Verified by cashier"),
            user,
            CancellationToken.None);
        var replay = await service.ConfirmBankTransferAsync(
            qr.Id,
            new ConfirmBankTransferRequest(
                confirmationId,
                qr.Version,
                "BANK-TRACE-881",
                "Verified by cashier"),
            user,
            CancellationToken.None);

        confirmed.Status.Should().Be(PaymentStatuses.Succeeded);
        confirmed.EvidenceReference.Should().Be("BANK-TRACE-881");
        replay.Should().BeEquivalentTo(confirmed);
        (await context.PaymentEvents.CountAsync()).Should().Be(2);
        var paidBill = (await context.Bills.FindAsync(bill.Id))!;
        paidBill.Status.Should().Be(BillStatuses.Paid);
        (await service.GetSettlementStatusAsync(sessionId, user, CancellationToken.None))
            .Settled.Should().BeFalse("a paid bill is not closed yet");

        var closed = await service.CloseBillAsync(
            bill.Id,
            new CloseBillRequest(paidBill.Version),
            user,
            CancellationToken.None);
        closed.Status.Should().Be(BillStatuses.Closed);
        (await service.GetSettlementStatusAsync(sessionId, user, CancellationToken.None))
            .Settled.Should().BeTrue();
    }

    [Fact]
    public async Task Stale_bill_version_cannot_create_payment()
    {
        await using var context = CreateContext();
        var user = new TestCurrentUser(Guid.NewGuid(), Guid.NewGuid());
        var bill = await SeedBillAsync(context, user, null, Guid.NewGuid(), 110000m);
        var service = CreateService(context);

        var action = () => service.CreateCashAsync(
            bill.Id,
            new CreatePaymentRequest(Guid.NewGuid(), bill.TotalAmount, 7),
            user,
            CancellationToken.None);

        var conflict = await action.Should().ThrowAsync<ConflictException>();
        conflict.Which.ErrorCode.Should().Be("payment.version_conflict");
        (await context.Payments.CountAsync()).Should().Be(0);
    }

    private static PaymentsDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<PaymentsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private static PaymentService CreateService(PaymentsDbContext context) =>
        new(
            context,
            new FixedTimeProvider(Now),
            new HttpContextAccessor(),
            NullLogger<PaymentService>.Instance);

    private static async Task<Bill> SeedBillAsync(
        PaymentsDbContext context,
        TestCurrentUser user,
        Guid? sessionId,
        Guid? orderId,
        decimal total)
    {
        var bill = new Bill(
            Guid.NewGuid(),
            user.BranchId!.Value,
            user.UserId!.Value,
            sessionId,
            orderId,
            $"BL-{Guid.NewGuid():N}",
            total,
            Now.DateTime);
        context.Bills.Add(bill);
        await context.SaveChangesAsync();
        return bill;
    }

    private sealed class TestCurrentUser(Guid branchId, Guid userId) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid? UserId { get; } = userId;
        public string? Name => "Payment Test Cashier";
        public string? Role => AicosRoles.Cashier;
        public Guid? BranchId { get; } = branchId;
        public bool IsInRole(string role) => role == AicosRoles.Cashier;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
