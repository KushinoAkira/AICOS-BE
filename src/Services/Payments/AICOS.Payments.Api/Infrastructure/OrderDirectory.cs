using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Messaging;
using AICOS.Contracts.Events;
using AICOS.Payments.Api.Domain;
using AICOS.Payments.Api.Features.Payments;
using AICOS.Payments.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.Payments.Api.Infrastructure;

/// <summary>What Payment needs to know about an order before taking money for it.</summary>
public sealed record OrderInfo(Guid Id, Guid BranchId, decimal FinalAmount, string Status, string PaymentStatus);

/// <summary>
/// Asks the Order service about orders, with the token of the caller (so the Order service applies its own brand, branch and customer rules).
/// An order the caller cannot see is reported as not found.
/// </summary>
public interface IOrderDirectory
{
    /// <summary>Throws a 422 <c>payment.order_not_found</c> when the order does not exist for the caller.</summary>
    Task<OrderInfo> GetAsync(Guid orderId, CancellationToken ct);

    /// <summary>Every order created or paid in the period, for the automatic reconciliation.</summary>
    Task<IReadOnlyList<OrderForReconciliation>> ListForReconciliationAsync(Guid? branchId, DateOnly from, DateOnly to, CancellationToken ct);
}

internal sealed class HttpOrderDirectory(HttpClient http) : IOrderDirectory
{
    private const int PageSize = 200;
    private const int MaxOrders = 5000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<OrderInfo> GetAsync(Guid orderId, CancellationToken ct)
    {
        using var response = await SendAsync(() => http.GetAsync($"/api/v1/orders/{orderId}", ct), ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            throw new BusinessRuleException("payment.order_not_found", "That order does not exist, or it is not yours to pay.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new DependencyUnavailableException("order");
        }

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
        return new OrderInfo(
            orderId, body.GetProperty("branchId").GetGuid(), body.GetProperty("finalAmount").GetDecimal(),
            body.GetProperty("status").GetString() ?? string.Empty, body.GetProperty("paymentStatus").GetString() ?? string.Empty);
    }

    public async Task<IReadOnlyList<OrderForReconciliation>> ListForReconciliationAsync(Guid? branchId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var orders = new List<OrderForReconciliation>();
        for (var offset = 0; offset < MaxOrders; offset += PageSize)
        {
            var url = $"/api/v1/orders?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}&includePaid=true&limit={PageSize}&offset={offset}" + (branchId is null ? string.Empty : $"&branchId={branchId}");
            using var response = await SendAsync(() => http.GetAsync(url, ct), ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new DependencyUnavailableException("order");
            }

            var page = await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
            foreach (var o in page.EnumerateArray())
            {
                var status = o.GetProperty("status").GetString();
                var paid = o.GetProperty("paymentStatus").GetString() is "Paid" or "Refunded";
                if (status == "Cancelled" && !paid)
                {
                    continue;   // never had money: nothing to compare
                }

                orders.Add(new OrderForReconciliation(o.GetProperty("id").GetGuid(), o.GetProperty("finalAmount").GetDecimal(), paid));
            }

            if (page.GetArrayLength() < PageSize)
            {
                break;
            }
        }

        return orders;
    }

    private static async Task<HttpResponseMessage> SendAsync(Func<Task<HttpResponseMessage>> call, CancellationToken ct)
    {
        try
        {
            return await call();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new DependencyUnavailableException("order");
        }
    }
}

/// <summary>The checks every payment goes through before money is taken (SCRUM-88, 89).</summary>
internal static class OrderChecks
{
    /// <summary>The order exists for this caller, is open, is not paid yet, belongs to the branch, and the amount is what is due.</summary>
    public static async Task<OrderInfo> EnsurePayableAsync(IOrderDirectory orders, Guid orderId, Guid branchId, decimal amount, CancellationToken ct)
    {
        var order = await orders.GetAsync(orderId, ct);
        if (order.Status == "Cancelled")
        {
            throw new BusinessRuleException("payment.order_cancelled", "The order was cancelled.");
        }

        if (order.PaymentStatus is "Paid" or "Refunded")
        {
            throw new ConflictException("This order is already paid.", "payment.already_paid");
        }

        if (order.BranchId != branchId)
        {
            throw new BusinessRuleException("payment.order_branch_mismatch", "The order belongs to another branch.");
        }

        if (order.FinalAmount != amount)
        {
            throw new BusinessRuleException("payment.amount_mismatch", $"The order is {order.FinalAmount:0} VND, not {amount:0}.");
        }

        return order;
    }
}

/// <summary>An order was cancelled: a payment still waiting for the customer is released so nobody pays for a dead order (BR-25).</summary>
public sealed class PaymentOrderCancelledHandler(PaymentsDbContext db) : IIntegrationEventHandler<OrderCancelledIntegrationEvent>
{
    public async Task HandleAsync(OrderCancelledIntegrationEvent @event, CancellationToken cancellationToken)
    {
        var pending = await db.Payments.Where(p => p.TenantId == @event.TenantId && p.OrderId == @event.OrderId && p.Status == PaymentStatus.Pending).ToListAsync(cancellationToken);
        if (pending.Count == 0)
        {
            return;
        }

        foreach (var payment in pending)
        {
            payment.Cancel("The order was cancelled.");
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
