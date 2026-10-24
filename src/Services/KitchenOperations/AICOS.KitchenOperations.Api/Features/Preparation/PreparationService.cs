using System.Data;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.KitchenOperations.Api.Domain;
using AICOS.KitchenOperations.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AICOS.KitchenOperations.Api.Features.Preparation;

public sealed class PreparationService(
    KitchenOperationsDbContext dbContext,
    TimeProvider clock,
    ILogger<PreparationService> logger)
{
    public async Task<PreparationQueueResponse> GetQueueAsync(
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        var branchId = RequireBranch(currentUser);
        var orders = await dbContext.Orders
            .AsNoTracking()
            .AsSplitQuery()
            .Include(order => order.Items)
            .Where(order =>
                order.BranchId == branchId &&
                order.Items.Any(item => item.Status != PreparationStatuses.Ready))
            .OrderBy(order => order.CreatedAt)
            .ToListAsync(cancellationToken);

        return new PreparationQueueResponse(
            branchId,
            orders.Select(ToResponse).ToList());
    }

    public Task<PreparationItemResponse> UpdateAsync(
        Guid itemId,
        UpdatePreparationStatusRequest request,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        if (itemId == Guid.Empty)
        {
            throw new ValidationFailedException("itemId", "Order item is required.");
        }

        var branchId = RequireBranch(currentUser);
        var actorId = currentUser.UserId ?? throw new ForbiddenException();
        var next = request.Status?.Trim().ToUpperInvariant();
        if (next is not PreparationStatuses.Preparing and not PreparationStatuses.Ready)
        {
            throw new ValidationFailedException(
                "status",
                "Status must be PREPARING or READY.");
        }

        return ExecuteSerializableAsync(async () =>
        {
            var item = await dbContext.Items
                .Include(candidate => candidate.Order)
                .SingleOrDefaultAsync(
                    candidate =>
                        candidate.Id == itemId &&
                        candidate.Order.BranchId == branchId,
                    cancellationToken)
                ?? throw new NotFoundException("Order item", itemId);
            var old = item.Status ?? PreparationStatuses.Pending;

            try
            {
                item.MoveTo(next);
            }
            catch (InvalidOperationException)
            {
                throw new ConflictException(
                    $"Cannot move item from {old} to {next}.",
                    "preparation.invalid_transition");
            }

            dbContext.Events.Add(new PreparationEvent(
                Guid.NewGuid(),
                item.Id,
                actorId,
                old,
                next,
                LocalNow()));
            await dbContext.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "User {UserId} moved item {ItemId} from {OldStatus} to {NewStatus}",
                actorId,
                item.Id,
                old,
                next);
            return ToItem(item);
        }, cancellationToken);
    }

    private async Task<T> ExecuteSerializableAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken)
    {
        if (!dbContext.Database.IsRelational())
        {
            return await operation();
        }

        var strategy = dbContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            try
            {
                dbContext.ChangeTracker.Clear();
                await using var transaction = await dbContext.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable,
                    cancellationToken);
                var result = await operation();
                await transaction.CommitAsync(cancellationToken);
                return result;
            }
            catch (Exception exception) when (IsSerializationFailure(exception))
            {
                throw new ConflictException(
                    "The item changed while the request was being processed. Reload and try again.",
                    "preparation.concurrent_update");
            }
        });
    }

    private DateTime LocalNow() => clock.GetLocalNow().DateTime;

    private static Guid RequireBranch(ICurrentUser currentUser) =>
        currentUser.BranchId is { } branchId && branchId != Guid.Empty
            ? branchId
            : throw new ForbiddenException("A branch-scoped access token is required.");

    private static bool IsSerializationFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            if (current is PostgresException postgres &&
                postgres.SqlState == PostgresErrorCodes.SerializationFailure)
            {
                return true;
            }

            if (current.InnerException is null)
            {
                break;
            }
        }

        return false;
    }

    private static PreparationOrderResponse ToResponse(PreparationOrder order) =>
        new(
            order.Id,
            order.OrderCode,
            order.OrderType,
            order.CreatedAt,
            order.Items
                .Where(item => item.Status != PreparationStatuses.Ready)
                .Select(ToItem)
                .ToList());

    private static PreparationItemResponse ToItem(PreparationItem item) =>
        new(
            item.Id,
            item.ProductName,
            item.VariantName,
            item.Quantity,
            item.Status ?? PreparationStatuses.Pending,
            item.Note);
}
