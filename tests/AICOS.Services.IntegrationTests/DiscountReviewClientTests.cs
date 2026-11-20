using System.Net;
using System.Net.Http.Json;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.Contracts.Billing;
using AICOS.Payments.Api;
using AICOS.Payments.Api.Features.SessionBilling;
using AICOS.Payments.Api.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AICOS.Services.IntegrationTests;

public sealed class DiscountReviewClientTests
{
    private sealed class Reply(string code) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
            { Content = JsonContent.Create(new { errorCode = code, detail = "private-internal-detail" }) });
    }

    [Theory]
    [InlineData("discount.review_required", "discount.review_required", false)]
    [InlineData("discount.review_required", "discount.review_required", true)]
    [InlineData("order.cancel_pending", "order.cancel_pending", false)]
    [InlineData("order.cancel_pending", "order.cancel_pending", true)]
    [InlineData("billing.legacy_payment", "billing.legacy_payment", false)]
    [InlineData("billing.legacy_payment", "billing.legacy_payment", true)]
    [InlineData("billing.empty", "billing.empty", false)]
    [InlineData("billing.empty", "billing.empty", true)]
    [InlineData("qr.session_closed", "qr.session_closed", false)]
    [InlineData("qr.session_closed", "qr.session_closed", true)]
    [InlineData("unexpected.internal", "billing.stale", false)]
    [InlineData("unexpected.internal", "billing.stale", true)]
    public async Task Real_typed_client_only_propagates_allowlisted_review_code(string upstream, string expected, bool freeze)
    {
        using var factory = new ServiceFactory<PaymentsService, PaymentsDbContext>(services =>
            services.AddHttpClient(nameof(IBillOrderingClient)).ConfigurePrimaryHttpMessageHandler(() => new Reply(upstream)),
            settings: new Dictionary<string, string?> { ["SessionBilling:MachineKey"] = new string('k', 40) });
        using var scope = factory.Services.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<IBillOrderingClient>();
        var action = async () => freeze ? await client.FreezeAsync(Guid.NewGuid(),
            new FreezeBillRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new string('a', 64)), default)
            : await client.QuoteAsync(Guid.NewGuid(), default);
        var error = await FluentActions.Awaiting(action).Should().ThrowAsync<ConflictException>();
        error.Which.ErrorCode.Should().Be(expected);
        error.Which.Message.Should().NotContain("private-internal-detail");
    }
}
