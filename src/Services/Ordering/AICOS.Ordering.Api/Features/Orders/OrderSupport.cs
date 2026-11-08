using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts;
using AICOS.Contracts.Events;
using AICOS.Ordering.Api.Domain;
using AICOS.Ordering.Api.Domain.Entities;
using AICOS.Ordering.Api.Domain.Enums;
using AICOS.Ordering.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.Ordering.Api.Features.Orders;

/// <summary>Who may see or touch which order (BR-04 brand isolation, BR-05 branch scoping).</summary>
internal static class OrderAccess
{
    public static readonly string[] CounterRoles = [AicosRoles.BrandAdmin, AicosRoles.BranchManager, AicosRoles.Cashier];

    public static bool IsStaff(ICurrentUser user) =>
        user.IsInRole(AicosRoles.BrandAdmin) || user.IsInRole(AicosRoles.BranchManager) || user.IsInRole(AicosRoles.Cashier) || user.IsInRole(AicosRoles.Barista);

    /// <summary>
    /// 404 for an order of another brand, or (for a customer) one that is not theirs; 403 for an order of another branch of the brand (staff).
    /// </summary>
    public static async Task<Order> LoadAsync(OrderingDbContext db, ICurrentUser user, Guid id, CancellationToken ct, bool track = true)
    {
        var tenantId = TenantScope.Require(user);
        var query = db.Orders.Include(o => o.Items).ThenInclude(i => i.Options).AsSplitQuery().Where(o => o.Id == id && o.TenantId == tenantId);
        var order = await (track ? query : query.AsNoTracking()).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Order", id);
        Ensure(user, order);
        return order;
    }

    public static void Ensure(ICurrentUser user, Order order)
    {
        if (IsStaff(user))
        {
            BranchAccess.EnsureCanAccess(user, order.BranchId);
        }
        else if (user.IsInRole(AicosRoles.Guest))
        {
            // A table token (see QrOrderEndpoints.PaymentAccessAsync): its user id is the table session.
            if (order.TableSessionId is null || order.TableSessionId != user.UserId)
            {
                throw new NotFoundException("Order", order.Id);
            }
        }
        else if (order.CustomerId is null || order.CustomerId != user.UserId)
        {
            throw new NotFoundException("Order", order.Id);
        }
    }

    public static PaymentMethod MethodOf(string provider) => provider switch
    {
        "Cash" => PaymentMethod.Cash,
        "VietQR" => PaymentMethod.BankTransfer,
        _ => PaymentMethod.EWallet,
    };
}

/// <summary>Sends the order events. A failure to publish is logged and never fails the change that already happened.</summary>
public sealed class OrderEventPublisher(IEventBus bus, IOrderNotifier notifier, ILogger<OrderEventPublisher> logger)
{
    /// <summary>The changes a member is told about with a stored notification; the others (created, preparing, delivered) only move the live screen.</summary>
    private static readonly HashSet<string> NotifiedKinds = ["Paid", "Ready", "Completed", "Cancelled", "NoShow", "Unfulfillable", "Remake"];

    /// <summary>
    /// A change of the order the customer should see: pushed live to the table screen and the member's screens, and, for the kinds above, sent to
    /// Customer Service which keeps it as a notification of the member. <paramref name="kind"/> is Created, Paid, Preparing, Ready, Remake, Delivered,
    /// Completed, Cancelled, NoShow or Unfulfillable.
    /// </summary>
    public async Task ChangedAsync(Order o, string kind, CancellationToken ct)
    {
        await notifier.OrderChangedAsync(o, kind, ct);
        if (o.CustomerId is { } customerId && NotifiedKinds.Contains(kind))
        {
            await SafeAsync(new OrderStatusChangedIntegrationEvent(
                o.TenantId, o.Id, o.OrderCode, o.BranchId, customerId, kind, kind == "Remake" ? $"Remake:{o.RemakeCount}" : kind, o.TableNumber, o.FinalAmount, DateTime.UtcNow), ct);
        }
    }

    public Task DeliveredAsync(Order order, CancellationToken ct) =>
        SafeAsync(new OrderDeliveredIntegrationEvent(order.TenantId, order.BranchId, order.Id,
            order.TableSessionId, order.DeliveredBy, order.DeliveredAtUtc!.Value), ct);

    public Task SessionClosedAsync(TableSession session, CancellationToken ct) => notifier.SessionClosedAsync(session, ct);

    /// <summary>The guests of a table asked for the bill: the counter hears about it through the Kitchen hub.</summary>
    public Task BillRequestedAsync(TableSession session, string method, CancellationToken ct) =>
        SafeAsync(new TableBillRequestedIntegrationEvent(session.TenantId, session.BranchId, session.Id, session.TableCode, method), ct);

    private static IReadOnlyList<OrderItemDto> Items(Order o) =>
        o.Items.Select(i => new OrderItemDto(i.ProductId, i.ProductName, i.UnitPrice, i.Quantity, BarNote(i))).ToList();

    /// <summary>The bar and the cup stickers read the note: the chosen options first, then the customer note.</summary>
    private static string? BarNote(OrderItem i)
    {
        var text = string.Join(" | ", new[] { i.OptionsText, i.Note }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return text.Length == 0 ? null : text.Length <= 300 ? text : text[..300];
    }

    public async Task CreatedAsync(Order o, CancellationToken ct)
    {
        await SafeAsync(new OrderCreatedIntegrationEvent(
            o.TenantId, o.Id, o.OrderCode, o.BranchId, o.CashierId, o.CustomerId, o.TableId, o.TableNumber, o.Type.ToString(), o.FinalAmount, o.IsPaid, o.PickupSlotStartUtc, Items(o), o.CreatedAtUtc, o.PayFirst), ct);
        await ChangedAsync(o, "Created", ct);
    }

    public async Task CancelledAsync(Order o, bool wasPaid, CancellationToken ct)
    {
        await SafeAsync(new OrderCancelledIntegrationEvent(
            o.TenantId, o.Id, o.BranchId, o.TableId, o.CancelledReason ?? string.Empty, wasPaid, Items(o), o.CancelledAtUtc ?? DateTime.UtcNow,
            o.OrderCode, o.CustomerId, o.PromotionCode, o.FinalAmount, o.Status.ToString()), ct);
        await ChangedAsync(o, o.Status.ToString(), ct);          // Cancelled, NoShow or Unfulfillable
    }

    public async Task PaidAsync(Order o, CancellationToken ct)
    {
        await SafeAsync(new OrderPaidIntegrationEvent(
            o.TenantId, o.Id, o.OrderCode, o.BranchId, o.CustomerId, o.FinalAmount, o.PaidAtUtc ?? DateTime.UtcNow), ct);
        await ChangedAsync(o, "Paid", ct);
    }

    public async Task CompletedAsync(Order o, CancellationToken ct)
    {
        await SafeAsync(new OrderCompletedIntegrationEvent(
            o.TenantId, o.Id, o.BranchId, o.TableId, o.FinalAmount, o.CompletedAtUtc ?? DateTime.UtcNow), ct);
        await ChangedAsync(o, "Completed", ct);
    }

    private async Task SafeAsync<TEvent>(TEvent @event, CancellationToken ct)
        where TEvent : IntegrationEvent
    {
        try
        {
            await bus.PublishAsync(@event, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not publish {Event}; the change itself was saved.", typeof(TEvent).Name);
        }
    }
}

/// <summary>Turns a report date into a UTC interval in the time zone of the shop (default Asia/Ho_Chi_Minh, <c>Ordering:TimeZone</c>).</summary>
public sealed class ShopTime(TimeProvider clock, IConfiguration configuration)
{
    private readonly TimeZoneInfo _zone = TimeZoneInfo.FindSystemTimeZoneById(configuration["Ordering:TimeZone"] ?? "Asia/Ho_Chi_Minh");

    public DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(UtcNow, _zone));

    public DateTime ToUtc(DateOnly date, TimeOnly time) => TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(time, DateTimeKind.Unspecified), _zone);

    public DateTime ToLocal(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), _zone);

    public DateTime StartOfDayUtc(DateOnly date) => TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), _zone);
}
