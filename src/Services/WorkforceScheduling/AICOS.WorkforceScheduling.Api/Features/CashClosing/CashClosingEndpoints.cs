using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Persistence;
using AICOS.BuildingBlocks.Security;
using AICOS.BuildingBlocks.Validation;
using AICOS.Contracts.Events;
using AICOS.WorkforceScheduling.Api.Domain;
using AICOS.WorkforceScheduling.Api.Features.Scheduling;
using AICOS.WorkforceScheduling.Api.Infrastructure;
using AICOS.WorkforceScheduling.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.WorkforceScheduling.Api.Features.CashClosing;

public sealed record SubmitCashClosingRequest(Guid? AssignmentId, decimal? CountedAmount, string? Note);

/// <summary>
/// <c>AcknowledgeExceptions</c>: the manager confirms although some order exceptions of the shift are still open (a refund pending or failed);
/// it is recorded in the audit history.
/// </summary>
public sealed record ConfirmCashClosingRequest(string? Note, bool? AcknowledgeExceptions = null);

/// <summary>What the system expects in the drawer for a shift, shown to the cashier before counting (the counted amount is still typed in blind).</summary>
public sealed record ExpectedCashResponse(Guid AssignmentId, decimal ExpectedAmount, decimal CashReceived, decimal CashRefunded, DateTime SinceUtc, DateTime AsOfUtc);

public sealed record CashTransactionLine(Guid? OrderId, decimal Amount, DateTime OccurredAtUtc, bool IsRefund);

/// <summary>What a manager reviews before closing a shift (SCRUM-49, 62): the cash that moved, and the orders cancelled after payment during the shift.</summary>
public sealed record CashClosingDetailsResponse(
    CashClosingResponse Closing,
    decimal CashReceived,
    decimal CashRefunded,
    List<CashTransactionLine> CashTransactions,
    List<OrderException> OrderExceptions,
    int UnresolvedExceptions);

public sealed record CashClosingResponse(
    Guid Id,
    Guid BranchId,
    Guid AssignmentId,
    Guid EmployeeId,
    string EmployeeName,
    DateOnly WorkDate,
    TimeOnly StartTime,
    TimeOnly EndTime,
    decimal ExpectedAmount,
    decimal CountedAmount,
    decimal Difference,
    string? CashierNote,
    CashClosingStatus Status,
    string? ManagerNote,
    DateTime SubmittedAtUtc,
    DateTime? ConfirmedAtUtc);

/// <summary>
/// UC-46 Reconcile End-of-Shift Cash (the Cashier counts the drawer) and UC-33 Confirm Shift Cash Closing (the Branch Manager reviews
/// any difference and closes the shift). Routes: /api/v1/workforce/cash-closings. The expected amount is computed by the system from
/// the cash recorded by that cashier during the shift (BR-55). A difference needs a note from the manager and is audited.
/// </summary>
public static class CashClosingEndpoints
{
    public static void MapCashClosingEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/cash-closings").WithTags("Cash closing");

        group.MapPost("/", SubmitAsync).RequireAuthorization(AicosPolicies.Staff);
        group.MapGet("/expected", ExpectedAsync).RequireAuthorization(AicosPolicies.Staff);
        group.MapGet("/mine", MineAsync).RequireAuthorization(AicosPolicies.Staff);
        group.MapGet("/", ListAsync).RequireAuthorization(AicosPolicies.Management);
        group.MapGet("/{id:guid}", GetAsync).RequireAuthorization(AicosPolicies.Staff).WithName("GetCashClosing");
        group.MapGet("/{id:guid}/details", DetailsAsync).RequireAuthorization(AicosPolicies.Management);
        group.MapPost("/{id:guid}/confirm", ConfirmAsync).RequireAuthorization(AicosPolicies.Management);
    }

    /// <summary>The Cashier records what was counted. The shift must have been worked (checked in), and each shift is closed once.</summary>
    private static async Task<IResult> SubmitAsync(
        SubmitCashClosingRequest r, WorkforceSchedulingDbContext db, ICurrentUser user, ShopClock shop, IEventBus bus, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        CommonRules.RequiredId(errors, "AssignmentId", r.AssignmentId, "Shift");
        if (r.CountedAmount is null or < 0 or > 1_000_000_000m)
        {
            errors.Add("CountedAmount", "The counted amount must be between 0 and 1,000,000,000 VND.");
        }

        CommonRules.MaxLength(errors, "Note", r.Note, "Note", 500);
        errors.ThrowIfAny();

        if (!user.IsInRole(AicosRoles.Cashier))
        {
            throw new ForbiddenException("Only a Cashier counts the cash of a shift.", "cash_closing.cashier_only");
        }

        var assignment = await ScheduleEndpoints.FindOwnAsync(db, user, r.AssignmentId!.Value, ct);
        if (!ShiftAssignment.ActiveStatuses.Contains(assignment.Status) || assignment.EffectiveCheckInUtc is null)
        {
            throw new BusinessRuleException("cash_closing.shift_not_worked", "You can only close the cash of a shift you checked in for.");
        }

        if (await db.CashClosings.AnyAsync(c => c.AssignmentId == assignment.Id, ct))
        {
            throw new ConflictException("The cash of this shift was already closed.", "cash_closing.duplicate");
        }

        var now = shop.UtcNow;
        var expected = (await ExpectedCashAsync(db, user, assignment, now, ct)).ExpectedAmount;

        var closing = new Domain.CashClosing(assignment.TenantId, assignment.BranchId, assignment.Id, assignment.EmployeeId, expected, r.CountedAmount!.Value, r.Note, now);
        db.CashClosings.Add(closing);
        await db.SaveChangesAsync(ct);
        var response = (await MapAsync(db, [closing], ct))[0];
        await bus.PublishAsync(new CashClosingSubmittedEvent(closing.TenantId, closing.BranchId, closing.Id, response.EmployeeName, response.ExpectedAmount, response.CountedAmount), ct);
        return Results.CreatedAtRoute("GetCashClosing", new { id = closing.Id }, response);
    }

    /// <summary>
    /// The cashier asks what the system expects in the drawer for a worked shift, before counting. Same figure and same shift rules as
    /// <see cref="SubmitAsync"/> (BR-55), so the preview never disagrees with the closing.
    /// </summary>
    private static async Task<ExpectedCashResponse> ExpectedAsync(
        Guid? assignmentId, WorkforceSchedulingDbContext db, ICurrentUser user, ShopClock shop, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        CommonRules.RequiredId(errors, "AssignmentId", assignmentId, "Shift");
        errors.ThrowIfAny();

        if (!user.IsInRole(AicosRoles.Cashier))
        {
            throw new ForbiddenException("Only a Cashier counts the cash of a shift.", "cash_closing.cashier_only");
        }

        var assignment = await ScheduleEndpoints.FindOwnAsync(db, user, assignmentId!.Value, ct);
        if (!ShiftAssignment.ActiveStatuses.Contains(assignment.Status) || assignment.EffectiveCheckInUtc is null)
        {
            throw new BusinessRuleException("cash_closing.shift_not_worked", "You can only close the cash of a shift you checked in for.");
        }

        if (await db.CashClosings.AnyAsync(c => c.AssignmentId == assignment.Id, ct))
        {
            throw new ConflictException("The cash of this shift was already closed.", "cash_closing.duplicate");
        }

        return await ExpectedCashAsync(db, user, assignment, shop.UtcNow, ct);
    }

    private static async Task<ExpectedCashResponse> ExpectedCashAsync(
        WorkforceSchedulingDbContext db, ICurrentUser user, ShiftAssignment assignment, DateTime now, CancellationToken ct)
    {
        var since = assignment.EffectiveCheckInUtc!.Value;
        var amounts = await db.CashTransactions.AsNoTracking()
            .Where(t => t.TenantId == assignment.TenantId && t.BranchId == assignment.BranchId && t.CashierUserId == user.UserId
                        && t.OccurredAtUtc >= since && t.OccurredAtUtc <= now)
            .Select(t => t.Amount).ToListAsync(ct);

        return new ExpectedCashResponse(
            assignment.Id, amounts.Sum(), amounts.Where(a => a > 0).Sum(), -amounts.Where(a => a < 0).Sum(), since, now);
    }

    private static async Task<List<CashClosingResponse>> MineAsync(WorkforceSchedulingDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var me = await EmployeeLookup.CurrentAsync(db, user, ct);
        var items = await db.CashClosings.AsNoTracking().Where(c => c.EmployeeId == me.Id).OrderByDescending(c => c.SubmittedAtUtc).Take(50).ToListAsync(ct);
        return await MapAsync(db, items, ct);
    }

    private static async Task<PagedResponse<CashClosingResponse>> ListAsync(
        WorkforceSchedulingDbContext db, ICurrentUser user, IBranchDirectory directory, Guid? branchId, CashClosingStatus? status, bool? withDifference,
        DateOnly? from, DateOnly? to, int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        var tenantId = TenantScope.Require(user);
        var branch = await WorkforceScope.FilterAsync(user, directory, branchId, ct);
        var query = db.CashClosings.AsNoTracking().Where(c => c.TenantId == tenantId);
        if (branch is not null)
        {
            query = query.Where(c => c.BranchId == branch);
        }

        if (status is not null)
        {
            query = query.Where(c => c.Status == status);
        }

        if (withDifference == true)
        {
            query = query.Where(c => c.Difference != 0);
        }

        if (from is not null)
        {
            var fromUtc = from.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            query = query.Where(c => c.SubmittedAtUtc >= fromUtc);
        }

        if (to is not null)
        {
            var toUtc = to.Value.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            query = query.Where(c => c.SubmittedAtUtc < toUtc);
        }

        var paged = await query.OrderByDescending(c => c.SubmittedAtUtc).ToPagedAsync(page, pageSize, c => c, ct);
        return new PagedResponse<CashClosingResponse>(await MapAsync(db, paged.Items, ct), paged.Page, paged.PageSize, paged.TotalCount);
    }

    private static async Task<CashClosingResponse> GetAsync(Guid id, WorkforceSchedulingDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var tenantId = TenantScope.Require(user);
        var closing = await db.CashClosings.FirstOrDefaultAsync(c => c.Id == id && c.TenantId == tenantId, ct) ?? throw new NotFoundException("Cash closing", id);

        if (user.IsInRole(AicosRoles.BrandAdmin) || user.IsInRole(AicosRoles.BranchManager))
        {
            BranchAccess.EnsureCanAccess(user, closing.BranchId);
        }
        else if (closing.EmployeeId != (await EmployeeLookup.CurrentAsync(db, user, ct)).Id)
        {
            throw new NotFoundException("Cash closing", id); // a cashier only sees their own closings
        }

        return (await MapAsync(db, [closing], ct))[0];
    }

    /// <summary>The cash movements and the order exceptions of a shift, for the manager who reviews the closing (SCRUM-49, 62).</summary>
    private static async Task<CashClosingDetailsResponse> DetailsAsync(
        Guid id, WorkforceSchedulingDbContext db, ICurrentUser user, ShopClock shop, IOrderExceptionDirectory orders, CancellationToken ct)
    {
        var tenantId = TenantScope.Require(user);
        var closing = await db.CashClosings.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id && c.TenantId == tenantId, ct) ?? throw new NotFoundException("Cash closing", id);
        BranchAccess.EnsureCanAccess(user, closing.BranchId);

        var (since, until, cashierUserId) = await ShiftWindowAsync(db, closing, ct);
        var cash = await db.CashTransactions.AsNoTracking()
            .Where(t => t.TenantId == closing.TenantId && t.BranchId == closing.BranchId && t.CashierUserId == cashierUserId && t.OccurredAtUtc >= since && t.OccurredAtUtc <= until)
            .OrderBy(t => t.OccurredAtUtc).ToListAsync(ct);
        var exceptions = await ExceptionsOfShiftAsync(orders, closing.BranchId, since, until, shop, ct);

        return new CashClosingDetailsResponse(
            (await MapAsync(db, [closing], ct))[0],
            cash.Where(t => t.Amount > 0).Sum(t => t.Amount),
            -cash.Where(t => t.Amount < 0).Sum(t => t.Amount),
            cash.Select(t => new CashTransactionLine(t.OrderId, t.Amount, t.OccurredAtUtc, t.Amount < 0)).ToList(),
            exceptions,
            exceptions.Count(e => e.IsUnresolved));
    }

    /// <summary>The window of a closing: from the check-in of the cashier to the moment the cash was counted.</summary>
    private static async Task<(DateTime Since, DateTime Until, Guid? CashierUserId)> ShiftWindowAsync(WorkforceSchedulingDbContext db, Domain.CashClosing closing, CancellationToken ct)
    {
        var assignment = await db.ShiftAssignments.AsNoTracking().FirstAsync(a => a.Id == closing.AssignmentId, ct);
        var employee = await db.Employees.AsNoTracking().FirstAsync(e => e.Id == closing.EmployeeId, ct);
        return (assignment.EffectiveCheckInUtc ?? closing.SubmittedAtUtc, closing.SubmittedAtUtc, employee.UserId);
    }

    /// <summary>Orders cancelled after payment at this branch during the shift. The Order service filters by day, so one day either side is asked for.</summary>
    private static async Task<List<OrderException>> ExceptionsOfShiftAsync(
        IOrderExceptionDirectory orders, Guid branchId, DateTime since, DateTime until, ShopClock shop, CancellationToken ct)
    {
        var from = DateOnly.FromDateTime(shop.ToLocal(since)).AddDays(-1);
        var to = DateOnly.FromDateTime(shop.ToLocal(until));
        var all = await orders.ListAsync(branchId, from, to, ct);
        return all.Where(e => e.CancelledAtUtc is not null && e.CancelledAtUtc >= since && e.CancelledAtUtc <= until).OrderBy(e => e.CancelledAtUtc).ToList();
    }

    /// <summary>
    /// BR-55: the Branch Manager reviews the difference and confirms; confirming a closing with a difference needs a note. A shift with order
    /// exceptions still open (money not yet paid back) is only confirmed when the manager says so (<c>AcknowledgeExceptions</c>).
    /// </summary>
    private static async Task<CashClosingResponse> ConfirmAsync(
        Guid id,
        ConfirmCashClosingRequest r,
        WorkforceSchedulingDbContext db,
        ICurrentUser user,
        ShopClock shop,
        IOrderExceptionDirectory orders,
        IEventBus bus,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var errors = new ValidationErrors();
        CommonRules.MaxLength(errors, "Note", r.Note, "Note", 500);
        errors.ThrowIfAny();

        var tenantId = TenantScope.Require(user);
        var closing = await db.CashClosings.FirstOrDefaultAsync(c => c.Id == id && c.TenantId == tenantId, ct) ?? throw new NotFoundException("Cash closing", id);
        BranchAccess.EnsureCanAccess(user, closing.BranchId);

        var (since, until, _) = await ShiftWindowAsync(db, closing, ct);
        var open = (await ExceptionsOfShiftAsync(orders, closing.BranchId, since, until, shop, ct)).Count(e => e.IsUnresolved);
        if (open > 0 && r.AcknowledgeExceptions != true)
        {
            throw new BusinessRuleException(
                "cash_closing.exceptions_unresolved",
                $"{open} order exception(s) of this shift still wait for a refund. Resolve them, or confirm with acknowledgeExceptions=true.");
        }

        var before = Snapshot(closing);
        closing.Confirm(r.Note, user.UserId, shop.UtcNow);
        await db.SaveChangesAsync(ct);

        await AuditTrail.PublishAsync(
            bus, user, loggers.CreateLogger("AICOS.Workforce.CashClosing"), closing.TenantId, "cash_closing.confirmed", "CashClosing", closing.Id, before,
            new { Closing = Snapshot(closing), OpenExceptionsAcknowledged = open });
        return (await MapAsync(db, [closing], ct))[0];
    }

    private static object Snapshot(Domain.CashClosing c) => new
    {
        c.ExpectedAmount, c.CountedAmount, c.Difference, Status = c.Status.ToString(), c.CashierNote, c.ManagerNote,
    };

    private static async Task<List<CashClosingResponse>> MapAsync(WorkforceSchedulingDbContext db, IReadOnlyList<Domain.CashClosing> items, CancellationToken ct)
    {
        var assignmentIds = items.Select(i => i.AssignmentId).ToList();
        var employeeIds = items.Select(i => i.EmployeeId).Distinct().ToList();
        var assignments = await db.ShiftAssignments.AsNoTracking().Where(a => assignmentIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        var names = await db.Employees.AsNoTracking().Where(e => employeeIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.FullName, ct);

        return items.Select(c => new CashClosingResponse(
            c.Id, c.BranchId, c.AssignmentId, c.EmployeeId, names.GetValueOrDefault(c.EmployeeId, string.Empty), assignments[c.AssignmentId].WorkDate,
            assignments[c.AssignmentId].StartTime, assignments[c.AssignmentId].EndTime, c.ExpectedAmount, c.CountedAmount, c.Difference, c.CashierNote,
            c.Status, c.ManagerNote, c.SubmittedAtUtc, c.ConfirmedAtUtc)).ToList();
    }
}

/// <summary>Order / payment flow -> Workforce: keeps the cash ledger that cash closings are computed from. Idempotent on the event id.</summary>
public sealed class CashPaymentRecordedHandler(WorkforceSchedulingDbContext db) : IIntegrationEventHandler<CashPaymentRecordedEvent>
{
    public async Task HandleAsync(CashPaymentRecordedEvent @event, CancellationToken cancellationToken)
    {
        if (await db.CashTransactions.AnyAsync(t => t.SourceEventId == @event.EventId, cancellationToken))
        {
            return;
        }

        db.CashTransactions.Add(new CashTransaction(
            @event.EventId, @event.TenantId, @event.BranchId, @event.CashierUserId, @event.OrderId, @event.Amount, @event.OccurredAtUtc));
        await db.SaveChangesAsync(cancellationToken);
    }
}
