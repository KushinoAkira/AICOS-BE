using System.Data;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.Payments.Api.Domain;
using AICOS.Payments.Api.Features.Billing;
using AICOS.Payments.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AICOS.Payments.Api.Features.Payments;

public sealed class PaymentService(
    PaymentsDbContext dbContext,
    TimeProvider clock,
    IHttpContextAccessor httpContextAccessor,
    ILogger<PaymentService> logger)
{
    public Task<PaymentResponse> CreateCashAsync(
        Guid billId,
        CreatePaymentRequest request,
        ICurrentUser currentUser,
        CancellationToken cancellationToken) =>
        CreatePaymentAsync(
            billId,
            request,
            PaymentMethods.Cash,
            currentUser,
            cancellationToken);

    public Task<PaymentResponse> CreateQrAsync(
        Guid billId,
        CreatePaymentRequest request,
        ICurrentUser currentUser,
        CancellationToken cancellationToken) =>
        CreatePaymentAsync(
            billId,
            request,
            PaymentMethods.BankTransfer,
            currentUser,
            cancellationToken);

    public Task<PaymentResponse> ConfirmBankTransferAsync(
        Guid paymentId,
        ConfirmBankTransferRequest request,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        ValidateConfirmation(paymentId, request);
        var branchId = RequireBranch(currentUser);
        var actorUserId = RequireUser(currentUser);
        var evidence = request.EvidenceReference.Trim();
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();

        return ExecuteSerializableAsync(async () =>
        {
            var replay = await dbContext.PaymentEvents
                .AsNoTracking()
                .Include(paymentEvent => paymentEvent.Payment)
                    .ThenInclude(payment => payment.Bill)
                .SingleOrDefaultAsync(
                    paymentEvent => paymentEvent.RequestId == request.ConfirmationRequestId,
                    cancellationToken);
            if (replay is not null)
            {
                if (replay.PaymentId != paymentId ||
                    replay.EventType != PaymentEventTypes.Confirmed ||
                    replay.EvidenceReference != evidence ||
                    replay.Reason != note ||
                    replay.Payment.Bill.BranchId != branchId)
                {
                    throw IdempotencyConflict();
                }

                return ToResponse(replay.Payment, replay.Payment.Bill);
            }

            var payment = await LoadPaymentAsync(paymentId, branchId, cancellationToken)
                ?? throw new NotFoundException("Payment", paymentId);
            EnsureVersion(payment.Version, request.ExpectedVersion, "payment");
            if (payment.Method != PaymentMethods.BankTransfer ||
                payment.Status != PaymentStatuses.Pending)
            {
                throw new ConflictException(
                    "Only a pending bank transfer can be confirmed.",
                    "payment.not_pending_bank_transfer");
            }

            var now = LocalNow();
            payment.ConfirmBankTransfer(actorUserId, evidence, now);
            var succeededAmount = payment.Bill.Payments
                .Where(item => item.Status == PaymentStatuses.Succeeded)
                .Sum(item => item.Amount);
            if (succeededAmount != payment.Bill.TotalAmount)
            {
                throw new ConflictException(
                    "Successful payments must exactly match the bill total.",
                    "payment.amount_mismatch");
            }

            payment.Bill.MarkPaid();
            dbContext.PaymentEvents.Add(new PaymentEvent(
                Guid.NewGuid(),
                payment.Id,
                request.ConfirmationRequestId,
                PaymentEventTypes.Confirmed,
                PaymentStatuses.Pending,
                PaymentStatuses.Succeeded,
                actorUserId,
                evidence,
                note,
                CorrelationId(),
                now));
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "User {UserId} confirmed bank transfer {PaymentId} for bill {BillId}",
                actorUserId,
                payment.Id,
                payment.BillId);
            return ToResponse(payment, payment.Bill);
        }, cancellationToken);
    }

    public Task<BillResponse> CloseBillAsync(
        Guid billId,
        CloseBillRequest request,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        if (billId == Guid.Empty)
        {
            throw new ValidationFailedException("billId", "Bill is required.");
        }

        if (request.ExpectedVersion < 0)
        {
            throw new ValidationFailedException("expectedVersion", "Expected version must not be negative.");
        }

        var branchId = RequireBranch(currentUser);
        var actorUserId = RequireUser(currentUser);
        return ExecuteSerializableAsync(async () =>
        {
            var bill = await LoadBillAsync(billId, branchId, cancellationToken)
                ?? throw new NotFoundException("Bill", billId);
            if (bill.Status == BillStatuses.Closed)
            {
                return BillingService.ToResponse(bill);
            }

            EnsureVersion(bill.Version, request.ExpectedVersion, "bill");
            var succeededAmount = bill.Payments
                .Where(payment => payment.Status == PaymentStatuses.Succeeded)
                .Sum(payment => payment.Amount);
            if (bill.Status != BillStatuses.Paid ||
                succeededAmount != bill.TotalAmount ||
                bill.Payments.Any(payment => payment.Status == PaymentStatuses.Pending))
            {
                throw new ConflictException(
                    "The bill cannot close until payment is fully settled.",
                    "billing.not_settled");
            }

            bill.Close(actorUserId, LocalNow());
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "User {UserId} closed bill {BillId} at branch {BranchId}",
                actorUserId,
                bill.Id,
                branchId);
            return BillingService.ToResponse(bill);
        }, cancellationToken);
    }

    public async Task<SettlementStatusResponse> GetSettlementStatusAsync(
        Guid sessionId,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ValidationFailedException("sessionId", "Session is required.");
        }

        var branchId = RequireBranch(currentUser);
        var bill = await dbContext.Bills
            .AsNoTracking()
            .Include(item => item.Payments)
            .SingleOrDefaultAsync(
                item => item.SessionId == sessionId && item.BranchId == branchId,
                cancellationToken);
        if (bill is null)
        {
            return new SettlementStatusResponse(
                sessionId, null, null, 0, 0, 0, false, false, 0);
        }

        var succeededAmount = bill.Payments
            .Where(payment => payment.Status == PaymentStatuses.Succeeded)
            .Sum(payment => payment.Amount);
        var hasPending = bill.Payments.Any(payment => payment.Status == PaymentStatuses.Pending);
        return new SettlementStatusResponse(
            sessionId,
            bill.Id,
            bill.Status,
            bill.TotalAmount,
            succeededAmount,
            Math.Max(0, bill.TotalAmount - succeededAmount),
            hasPending,
            bill.Status == BillStatuses.Closed &&
                succeededAmount == bill.TotalAmount &&
                !hasPending,
            bill.Version);
    }

    private Task<PaymentResponse> CreatePaymentAsync(
        Guid billId,
        CreatePaymentRequest request,
        string method,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        ValidateCreation(billId, request);
        var branchId = RequireBranch(currentUser);
        var actorUserId = RequireUser(currentUser);
        return ExecuteSerializableAsync(async () =>
        {
            var replay = await dbContext.Payments
                .AsNoTracking()
                .Include(payment => payment.Bill)
                .SingleOrDefaultAsync(payment => payment.Id == request.RequestId, cancellationToken);
            if (replay is not null)
            {
                if (replay.BillId != billId || replay.Bill.BranchId != branchId ||
                    replay.Method != method || replay.Amount != request.Amount)
                {
                    throw IdempotencyConflict();
                }

                return ToResponse(replay, replay.Bill);
            }

            var bill = await LoadBillAsync(billId, branchId, cancellationToken)
                ?? throw new NotFoundException("Bill", billId);
            EnsureVersion(bill.Version, request.ExpectedBillVersion, "bill");
            if (bill.Status != BillStatuses.Unpaid)
            {
                throw new ConflictException(
                    "Only an unpaid bill can accept a payment.",
                    "billing.not_unpaid");
            }

            if (bill.Payments.Any(payment => payment.Status == PaymentStatuses.Pending))
            {
                throw new ConflictException(
                    "Resolve the pending payment before creating another payment.",
                    "payment.pending_exists");
            }

            var succeededAmount = bill.Payments
                .Where(payment => payment.Status == PaymentStatuses.Succeeded)
                .Sum(payment => payment.Amount);
            var outstanding = bill.TotalAmount - succeededAmount;
            if (request.Amount != outstanding)
            {
                throw new ConflictException(
                    "Payment amount must equal the outstanding bill amount.",
                    "payment.amount_mismatch");
            }

            var now = LocalNow();
            Payment payment;
            PaymentEvent paymentEvent;
            if (method == PaymentMethods.Cash)
            {
                payment = Payment.CreateCash(request.RequestId, bill.Id, actorUserId, request.Amount, now);
                bill.MarkPaid();
                paymentEvent = NewEvent(
                    payment,
                    request.RequestId,
                    PaymentEventTypes.Confirmed,
                    null,
                    PaymentStatuses.Succeeded,
                    actorUserId,
                    null,
                    "Cash received at point of sale.",
                    now);
            }
            else
            {
                payment = Payment.CreateBankTransfer(
                    request.RequestId,
                    bill.Id,
                    actorUserId,
                    request.Amount,
                    $"AICOS-{request.RequestId:N}".ToUpperInvariant(),
                    now);
                bill.RegisterPendingPayment();
                paymentEvent = NewEvent(
                    payment,
                    request.RequestId,
                    PaymentEventTypes.Created,
                    null,
                    PaymentStatuses.Pending,
                    actorUserId,
                    null,
                    null,
                    now);
            }

            dbContext.AddRange(payment, paymentEvent);
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "User {UserId} created {Method} payment {PaymentId} for bill {BillId} with status {Status}",
                actorUserId,
                method,
                payment.Id,
                bill.Id,
                payment.Status);
            return ToResponse(payment, bill);
        }, cancellationToken);
    }

    private PaymentEvent NewEvent(
        Payment payment,
        Guid requestId,
        string eventType,
        string? fromStatus,
        string toStatus,
        Guid actorUserId,
        string? evidenceReference,
        string? reason,
        DateTime now) =>
        new(
            Guid.NewGuid(),
            payment.Id,
            requestId,
            eventType,
            fromStatus,
            toStatus,
            actorUserId,
            evidenceReference,
            reason,
            CorrelationId(),
            now);

    private async Task<Bill?> LoadBillAsync(
        Guid billId,
        Guid branchId,
        CancellationToken cancellationToken) =>
        await dbContext.Bills
            .Include(bill => bill.Items)
            .Include(bill => bill.Payments)
            .SingleOrDefaultAsync(
                bill => bill.Id == billId && bill.BranchId == branchId,
                cancellationToken);

    private async Task<Payment?> LoadPaymentAsync(
        Guid paymentId,
        Guid branchId,
        CancellationToken cancellationToken) =>
        await dbContext.Payments
            .Include(payment => payment.Bill)
                .ThenInclude(bill => bill.Items)
            .Include(payment => payment.Bill)
                .ThenInclude(bill => bill.Payments)
            .SingleOrDefaultAsync(
                payment => payment.Id == paymentId && payment.Bill.BranchId == branchId,
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
            catch (Exception exception) when (IsConcurrencyFailure(exception))
            {
                throw new ConflictException(
                    "Payment state changed while processing. Reload and try again.",
                    "payment.concurrent_update");
            }
        });
    }

    private static bool IsConcurrencyFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            if (current is DbUpdateConcurrencyException)
            {
                return true;
            }

            if (current is PostgresException postgres &&
                postgres.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.UniqueViolation)
            {
                return true;
            }

            if (current.InnerException is null) break;
        }

        return false;
    }

    private string? CorrelationId()
    {
        var context = httpContextAccessor.HttpContext;
        return context?.Request.Headers["X-Correlation-Id"].FirstOrDefault()
            ?? context?.TraceIdentifier;
    }

    private DateTime LocalNow() => clock.GetLocalNow().DateTime;

    private static void ValidateCreation(Guid billId, CreatePaymentRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (billId == Guid.Empty) errors["billId"] = ["Bill is required."];
        if (request.RequestId == Guid.Empty) errors["requestId"] = ["Request ID is required."];
        if (request.Amount <= 0) errors["amount"] = ["Amount must be greater than zero."];
        if (request.ExpectedBillVersion < 0) errors["expectedBillVersion"] = ["Expected version must not be negative."];
        if (errors.Count > 0) throw new ValidationFailedException(errors);
    }

    private static void ValidateConfirmation(Guid paymentId, ConfirmBankTransferRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (paymentId == Guid.Empty) errors["paymentId"] = ["Payment is required."];
        if (request.ConfirmationRequestId == Guid.Empty) errors["confirmationRequestId"] = ["Confirmation request ID is required."];
        if (request.ExpectedVersion < 0) errors["expectedVersion"] = ["Expected version must not be negative."];
        if (string.IsNullOrWhiteSpace(request.EvidenceReference) || request.EvidenceReference.Trim().Length > 255)
            errors["evidenceReference"] = ["Evidence reference is required and must be at most 255 characters."];
        if (request.Note?.Trim().Length > 500) errors["note"] = ["Note must be at most 500 characters."];
        if (errors.Count > 0) throw new ValidationFailedException(errors);
    }

    private static void EnsureVersion(long actual, long expected, string resource)
    {
        if (actual != expected)
        {
            throw new ConflictException(
                $"The {resource} was modified. Reload and try again.",
                "payment.version_conflict");
        }
    }

    private static ConflictException IdempotencyConflict() =>
        new(
            "The request ID was already used with different payment data.",
            "payment.idempotency_conflict");

    private static Guid RequireBranch(ICurrentUser currentUser) =>
        currentUser.BranchId is { } branchId && branchId != Guid.Empty
            ? branchId
            : throw new ForbiddenException("A branch-scoped access token is required.");

    private static Guid RequireUser(ICurrentUser currentUser) =>
        currentUser.UserId is { } userId && userId != Guid.Empty
            ? userId
            : throw new ForbiddenException("An authenticated staff identity is required.");

    internal static PaymentResponse ToResponse(Payment payment, Bill bill) =>
        new(
            payment.Id,
            payment.BillId,
            payment.Method,
            payment.Status ?? PaymentStatuses.Pending,
            payment.Amount,
            payment.TransactionReference,
            payment.EvidenceReference,
            payment.CreatedByUserId,
            payment.ConfirmedByUserId,
            payment.ConfirmedAt,
            payment.PaidAt,
            payment.CreatedAt,
            payment.Version,
            bill.Status ?? BillStatuses.Unpaid,
            bill.Version);
}
