using System.Data;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.Payments.Api.Domain;
using AICOS.Payments.Api.Integrations;
using AICOS.Payments.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AICOS.Payments.Api.Features.Billing;

public sealed class BillingService(
    PaymentsDbContext dbContext,
    IOrderBillingClient orders,
    TimeProvider clock,
    ILogger<BillingService> logger)
{
    public async Task<BillResponse> CreateAsync(
        CreateBillRequest request,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        Validate(request);
        var branchId = RequireBranch(currentUser);
        var actorUserId = RequireUser(currentUser);

        var sourceOrders = await ResolveOrdersAsync(request, cancellationToken);
        if (sourceOrders.Count == 0)
        {
            throw new BusinessRuleException(
                "billing.empty_session",
                "The table session does not have any orders to bill.");
        }

        var total = sourceOrders.Sum(order => order.TotalAmount);
        if (total <= 0)
        {
            throw new BusinessRuleException(
                "billing.invalid_total",
                "The bill total must be greater than zero.");
        }

        return await ExecuteSerializableAsync(async () =>
        {
            var existingById = await LoadAsync(request.RequestId, branchId, cancellationToken);
            if (existingById is not null)
            {
                EnsureIdempotentRequest(existingById, request);
                return ToResponse(existingById);
            }

            var targetAlreadyBilled = await dbContext.Bills.AnyAsync(
                bill =>
                    request.SessionId != null && bill.SessionId == request.SessionId ||
                    request.OrderId != null && bill.OrderId == request.OrderId,
                cancellationToken);
            if (targetAlreadyBilled)
            {
                throw new ConflictException(
                    "A bill already exists for this order or table session.",
                    "billing.already_exists");
            }

            var now = LocalNow();
            var bill = new Bill(
                request.RequestId,
                branchId,
                actorUserId,
                request.SessionId,
                request.OrderId,
                $"BL-{request.RequestId:N}",
                total,
                now);
            foreach (var order in sourceOrders)
            {
                foreach (var item in order.Items)
                {
                    var amount =
                        (item.UnitPrice + item.Options.Sum(option => option.ExtraPrice)) *
                        item.Quantity;
                    bill.Items.Add(new BillItem(
                        Guid.NewGuid(),
                        bill.Id,
                        item.Id,
                        item.Quantity,
                        amount));
                }
            }

            dbContext.Bills.Add(bill);
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "User {UserId} created bill {BillId} for session {SessionId} order {OrderId}",
                currentUser.UserId,
                bill.Id,
                bill.SessionId,
                bill.OrderId);
            return ToResponse(bill);
        }, cancellationToken);
    }

    public async Task<BillResponse> GetAsync(
        Guid billId,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        if (billId == Guid.Empty)
        {
            throw new ValidationFailedException("billId", "Bill is required.");
        }

        var branchId = RequireBranch(currentUser);
        var bill = await LoadAsync(billId, branchId, cancellationToken) ??
            throw new NotFoundException("Bill", billId);
        await VerifyAccessAsync(bill, cancellationToken);
        return ToResponse(bill);
    }

    public async Task<BillResponse> GetBySourceAsync(
        Guid? sessionId,
        Guid? orderId,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        ValidateSource(sessionId, orderId);
        var branchId = RequireBranch(currentUser);
        await VerifySourceAccessAsync(sessionId, orderId, cancellationToken);

        var bill = await dbContext.Bills
            .AsNoTracking()
            .AsSplitQuery()
            .Include(item => item.Items)
            .Include(item => item.Payments)
            .SingleOrDefaultAsync(
                item =>
                    item.BranchId == branchId &&
                    (sessionId != null && item.SessionId == sessionId ||
                     orderId != null && item.OrderId == orderId),
                cancellationToken) ??
            throw new NotFoundException(
                "Bill",
                sessionId ?? orderId!.Value);

        return ToResponse(bill);
    }

    private async Task<IReadOnlyList<BillingOrderContract>> ResolveOrdersAsync(
        CreateBillRequest request,
        CancellationToken cancellationToken)
    {
        if (request.SessionId is { } sessionId)
        {
            return await orders.GetBySessionAsync(sessionId, cancellationToken);
        }

        var order = await orders.GetAsync(request.OrderId!.Value, cancellationToken);
        if (order.SessionId is not null)
        {
            throw new BusinessRuleException(
                "billing.session_required",
                "Dine-in orders must be billed by table session.");
        }

        return [order];
    }

    private async Task VerifyAccessAsync(Bill bill, CancellationToken cancellationToken)
    {
        await VerifySourceAccessAsync(
            bill.SessionId,
            bill.OrderId,
            cancellationToken);
    }

    private async Task VerifySourceAccessAsync(
        Guid? sessionId,
        Guid? orderId,
        CancellationToken cancellationToken)
    {
        if (sessionId is { } sourceSessionId)
        {
            var sourceOrders = await orders.GetBySessionAsync(
                sourceSessionId,
                cancellationToken);
            if (sourceOrders.Count == 0)
            {
                throw new NotFoundException("Table session", sourceSessionId);
            }

            return;
        }

        if (orderId is { } sourceOrderId)
        {
            await orders.GetAsync(sourceOrderId, cancellationToken);
            return;
        }

        throw new ConflictException(
            "The bill is not linked to an order or table session.",
            "billing.invalid_source");
    }

    private async Task<Bill?> LoadAsync(
        Guid billId,
        Guid branchId,
        CancellationToken cancellationToken) =>
        await dbContext.Bills
            .AsSplitQuery()
            .Include(bill => bill.Items)
            .Include(bill => bill.Payments)
            .SingleOrDefaultAsync(
                bill => bill.Id == billId && bill.BranchId == branchId,
                cancellationToken);

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
                    "The billing source changed while processing. Reload and try again.",
                    "billing.concurrent_update");
            }
        });
    }

    private DateTime LocalNow() => clock.GetLocalNow().DateTime;

    private static void Validate(CreateBillRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.RequestId == Guid.Empty)
        {
            errors["requestId"] = ["Request ID is required."];
        }

        if ((request.SessionId is null) == (request.OrderId is null))
        {
            errors["source"] = ["Provide exactly one sessionId or orderId."];
        }
        else if (request.SessionId == Guid.Empty || request.OrderId == Guid.Empty)
        {
            errors["source"] = ["Billing source ID must not be empty."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationFailedException(errors);
        }
    }

    private static void ValidateSource(Guid? sessionId, Guid? orderId)
    {
        var errors = new Dictionary<string, string[]>();
        if ((sessionId is null) == (orderId is null))
        {
            errors["source"] = ["Provide exactly one sessionId or orderId."];
        }
        else if (sessionId == Guid.Empty || orderId == Guid.Empty)
        {
            errors["source"] = ["Billing source ID must not be empty."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationFailedException(errors);
        }
    }

    private static Guid RequireBranch(ICurrentUser currentUser) =>
        currentUser.BranchId is { } branchId && branchId != Guid.Empty
            ? branchId
            : throw new ForbiddenException("A branch-scoped access token is required.");

    private static Guid RequireUser(ICurrentUser currentUser) =>
        currentUser.UserId is { } userId && userId != Guid.Empty
            ? userId
            : throw new ForbiddenException("An authenticated staff identity is required.");

    private static void EnsureIdempotentRequest(Bill bill, CreateBillRequest request)
    {
        if (bill.SessionId != request.SessionId || bill.OrderId != request.OrderId)
        {
            throw new ConflictException(
                "The request ID was already used for another bill source.",
                "billing.idempotency_conflict");
        }
    }

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

    internal static BillResponse ToResponse(Bill bill) =>
        new(
            bill.Id,
            bill.BillCode,
            bill.Status ?? BillStatuses.Unpaid,
            bill.BranchId,
            bill.CreatedByUserId,
            bill.SessionId,
            bill.OrderId,
            bill.TotalAmount,
            bill.PrintStatus,
            bill.ClosedAt,
            bill.ClosedByUserId,
            bill.Version,
            bill.CreatedAt,
            bill.Items.Select(item => new BillItemResponse(
                item.Id,
                item.OrderItemId,
                item.Quantity,
                item.Amount)).ToList(),
            bill.Payments.Select(payment => new BillPaymentResponse(
                payment.Id,
                payment.Method,
                payment.Status ?? PaymentStatuses.Pending,
                payment.Amount,
                payment.TransactionReference,
                payment.PaidAt,
                payment.CreatedByUserId,
                payment.ConfirmedByUserId,
                payment.ConfirmedAt,
                payment.EvidenceReference,
                payment.Version)).ToList());
}
