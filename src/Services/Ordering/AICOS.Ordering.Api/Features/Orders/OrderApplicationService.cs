using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.Ordering.Api.Domain;
using AICOS.Ordering.Api.Integrations;
using AICOS.Ordering.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.Ordering.Api.Features.Orders;

public sealed class OrderApplicationService(
    OrderingDbContext dbContext,
    ITableSessionClient tables,
    IMenuCatalogClient menu,
    TimeProvider clock,
    ILogger<OrderApplicationService> logger)
{
    public async Task<OrderResponse> CreateAsync(CreateOrderRequest request, ICurrentUser user, CancellationToken cancellationToken)
    {
        Validate(request);
        var branchId = user.BranchId ?? throw new ForbiddenException("A branch-scoped access token is required.");
        var userId = user.UserId ?? throw new ForbiddenException();
        var existing = await LoadAsync(request.RequestId, branchId, cancellationToken);
        if (existing is not null) return ToResponse(existing);

        var type = request.OrderType.Trim().ToUpperInvariant();
        if (type == OrderTypes.DineIn)
        {
            if (request.SessionId is null) throw new ValidationFailedException("sessionId", "Dine-in order requires a table session.");
            var session = await tables.GetAsync(request.SessionId.Value, cancellationToken);
            if (session.BranchId != branchId || session.Status != "OPEN" || session.TableIds.Count == 0)
                throw new BusinessRuleException("order.invalid_session", "The table session is not open in this branch.");
        }
        else if (request.SessionId is not null)
        {
            throw new ValidationFailedException("sessionId", "Takeaway order must not have a table session.");
        }

        var catalog = await menu.GetAsync(cancellationToken);
        if (catalog.BranchId != branchId) throw new ForbiddenException("Catalog branch does not match the access token.");
        var products = catalog.Categories.SelectMany(x => x.Products).ToList();
        var now = clock.GetLocalNow().DateTime;
        DateTime? pickupTime = request.PickupTime is null
            ? null
            : DateTime.SpecifyKind(request.PickupTime.Value, DateTimeKind.Unspecified);
        var order = new Order(request.RequestId, branchId, request.SessionId, $"BK-{request.RequestId:N}", type, pickupTime, now);
        decimal total = 0;
        foreach (var requested in request.Items)
        {
            var product = products.SingleOrDefault(x => x.Variants.Any(v => v.Id == requested.VariantId))
                ?? throw new BusinessRuleException("order.variant_unavailable", $"Variant '{requested.VariantId}' is unavailable.");
            var variant = product.Variants.Single(x => x.Id == requested.VariantId);
            var allowedOptions = product.OptionGroups.SelectMany(x => x.Options).ToDictionary(x => x.Id);
            var selected = requested.OptionIds.Distinct().Select(id => allowedOptions.GetValueOrDefault(id)
                ?? throw new BusinessRuleException("order.option_unavailable", $"Option '{id}' is unavailable.")).ToList();
            foreach (var group in product.OptionGroups)
            {
                var selectedCount = group.Options.Count(option => requested.OptionIds.Contains(option.Id));
                if (selectedCount < group.MinSelect || selectedCount > group.MaxSelect)
                    throw new BusinessRuleException("order.invalid_option_selection", "Selected options do not satisfy the option group rules.");
            }
            var item = new OrderItem(Guid.NewGuid(), order.Id, variant.Id, product.Name, variant.SizeName, requested.Quantity, variant.Price, requested.Note?.Trim());
            foreach (var option in selected) item.Options.Add(new OrderItemOption(Guid.NewGuid(), item.Id, option.Id, option.Name, option.ExtraPrice));
            item.Events.Add(new OrderItemEvent(Guid.NewGuid(), item.Id, userId, now));
            order.Items.Add(item);
            total += (variant.Price + selected.Sum(x => x.ExtraPrice)) * requested.Quantity;
        }
        order.SetTotals(total, total);
        dbContext.Orders.Add(order);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            dbContext.ChangeTracker.Clear();
            var concurrent = await LoadAsync(request.RequestId, branchId, cancellationToken);
            if (concurrent is not null) return ToResponse(concurrent);
            throw;
        }
        logger.LogInformation("User {UserId} created order {OrderId} at branch {BranchId}", userId, order.Id, branchId);
        return ToResponse(order);
    }

    public async Task<OrderResponse> GetAsync(Guid id, ICurrentUser user, CancellationToken cancellationToken)
    {
        var branchId = user.BranchId ?? throw new ForbiddenException("A branch-scoped access token is required.");
        return ToResponse(await LoadAsync(id, branchId, cancellationToken) ?? throw new NotFoundException("Order", id));
    }

    public async Task<SessionOrdersResponse> GetBySessionAsync(
        Guid sessionId,
        ICurrentUser user,
        CancellationToken cancellationToken)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ValidationFailedException("sessionId", "Session is required.");
        }

        var branchId = user.BranchId ??
            throw new ForbiddenException("A branch-scoped access token is required.");
        var orders = await dbContext.Orders
            .AsNoTracking()
            .AsSplitQuery()
            .Include(order => order.Items)
                .ThenInclude(item => item.Options)
            .Include(order => order.Items)
                .ThenInclude(item => item.Events)
            .Where(order => order.BranchId == branchId && order.SessionId == sessionId)
            .OrderBy(order => order.CreatedAt)
            .ToListAsync(cancellationToken);

        return new SessionOrdersResponse(
            sessionId,
            orders.Select(ToResponse).ToList());
    }

    private async Task<Order?> LoadAsync(Guid id, Guid branchId, CancellationToken cancellationToken) =>
        await dbContext.Orders.AsSplitQuery().Include(x => x.Items).ThenInclude(x => x.Options)
            .Include(x => x.Items).ThenInclude(x => x.Events)
            .SingleOrDefaultAsync(x => x.Id == id && x.BranchId == branchId, cancellationToken);

    private static void Validate(CreateOrderRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.RequestId == Guid.Empty) errors["requestId"] = ["Request ID is required."];
        var type = request.OrderType?.Trim().ToUpperInvariant();
        if (type is not OrderTypes.DineIn and not OrderTypes.Takeaway) errors["orderType"] = ["Order type must be DINE_IN or TAKEAWAY."];
        if (request.Items is null || request.Items.Count is 0 or > 50) errors["items"] = ["Order must contain between 1 and 50 items."];
        else if (request.Items.Any(x => x.VariantId == Guid.Empty || x.Quantity is < 1 or > 99 || x.Note?.Length > 500 || x.OptionIds is null)) errors["items"] = ["Each item must have a variant, quantity 1-99, options, and note at most 500 characters."];
        if (errors.Count > 0) throw new ValidationFailedException(errors);
    }

    private static OrderResponse ToResponse(Order order) => new(order.Id, order.OrderCode, order.OrderType, order.Status ?? OrderStatuses.Pending, order.SessionId, order.PickupTime, order.Subtotal ?? 0, order.TotalAmount ?? 0, order.CreatedAt,
        order.Items.Select(item => new OrderItemResponse(item.Id, item.VariantId, item.ProductName, item.VariantName, item.Quantity, item.UnitPrice, item.Status ?? OrderStatuses.Pending, item.Note,
            item.Options.Select(option => new OrderOptionResponse(option.OptionId, option.OptionName, option.ExtraPrice)).ToList(), item.Events.OrderBy(x => x.CreatedAt).FirstOrDefault()?.ActorUserId)).ToList());
}
