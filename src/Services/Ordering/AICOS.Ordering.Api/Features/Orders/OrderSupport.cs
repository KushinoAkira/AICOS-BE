using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts;
using AICOS.Contracts.Events;
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
        var query = db.Orders.Include(o => o.Items).AsSplitQuery().Where(o => o.Id == id && o.TenantId == tenantId);
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
public sealed class OrderEventPublisher(IEventBus bus, ILogger<OrderEventPublisher> logger)
{
    private static IReadOnlyList<OrderItemDto> Items(Order o) =>
        o.Items.Select(i => new OrderItemDto(i.ProductId, i.ProductName, i.UnitPrice, i.Quantity, i.Note)).ToList();

    public Task CreatedAsync(Order o, CancellationToken ct) => SafeAsync(new OrderCreatedIntegrationEvent(
        o.TenantId, o.Id, o.OrderCode, o.BranchId, o.CashierId, o.CustomerId, o.TableId, o.TableNumber, o.Type.ToString(), o.FinalAmount, o.IsPaid, null, Items(o), o.CreatedAtUtc), ct);

    public Task CancelledAsync(Order o, bool wasPaid, CancellationToken ct) => SafeAsync(new OrderCancelledIntegrationEvent(
        o.TenantId, o.Id, o.BranchId, o.TableId, o.CancelledReason ?? string.Empty, wasPaid, Items(o), o.CancelledAtUtc ?? DateTime.UtcNow), ct);

    public Task CompletedAsync(Order o, CancellationToken ct) => SafeAsync(new OrderCompletedIntegrationEvent(
        o.TenantId, o.Id, o.BranchId, o.TableId, o.FinalAmount, o.CompletedAtUtc ?? DateTime.UtcNow), ct);

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

    public DateTime StartOfDayUtc(DateOnly date) => TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), _zone);
}
