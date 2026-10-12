using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Persistence;
using AICOS.BuildingBlocks.Security;
using AICOS.BuildingBlocks.Validation;
using AICOS.TableReservation.Api.Domain;
using AICOS.TableReservation.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.TableReservation.Api.Features.Reservations;

/// <summary>
/// <see cref="BranchId"/>: required for customers; staff always book in their own branch.
/// <see cref="ReservedAt"/>: ISO-8601 with offset, e.g. "2026-10-20T19:00:00+07:00".
/// </summary>
public sealed record ReservationRequest(
    Guid? BranchId, Guid? TableId, string? CustomerName, string? CustomerPhone, int? PartySize,
    DateTimeOffset? ReservedAt, int? DurationMinutes, string? Note);

public sealed record ConfirmReservationRequest(Guid? TableId);

public sealed record CancelReservationRequest(string? Reason);

public sealed record ReservationResponse(
    Guid Id, Guid BranchId, Guid? TableId, string? TableCode, Guid? CustomerUserId, string CustomerName, string CustomerPhone,
    int PartySize, DateTime ReservedAtUtc, DateTime EndsAtUtc, int DurationMinutes, ReservationStatus Status, string? Note,
    string? CancelReason, DateTime CreatedAtUtc);

public sealed record AvailableTableResponse(Guid Id, string TableCode, int Capacity, string? Area);

/// <summary>
/// UC-29 Reserve Table Online (Customer) and UC-18 Manage Table Reservation (Cashier/Manager).
/// Routes: /api/v1/tables/reservations and /api/v1/tables/availability.
/// Flow: Pending -> Confirmed (table assigned) -> Seated -> Completed ; Pending/Confirmed -> Cancelled / NoShow.
/// BR-10: a table can never hold two active reservations whose time slots overlap (409).
/// </summary>
public static class ReservationEndpoints
{
    public const int DefaultDurationMinutes = 120;
    public const int MinLeadMinutes = 15;
    public const int MaxDaysAhead = 30;

    public static void MapReservationEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/reservations").WithTags("Reservations");

        group.MapPost("/", CreateAsync).RequireAuthorization();
        group.MapGet("/", ListAsync).RequireAuthorization(AicosPolicies.Staff);
        group.MapGet("/mine", MineAsync).RequireAuthorization();
        group.MapGet("/{id:guid}", GetAsync).RequireAuthorization().WithName("GetReservation");
        group.MapPost("/{id:guid}/confirm", ConfirmAsync).RequireAuthorization(AicosPolicies.Staff);
        group.MapPost("/{id:guid}/seat", SeatAsync).RequireAuthorization(AicosPolicies.Staff);
        group.MapPost("/{id:guid}/complete", CompleteAsync).RequireAuthorization(AicosPolicies.Staff);
        group.MapPost("/{id:guid}/no-show", NoShowAsync).RequireAuthorization(AicosPolicies.Staff);
        group.MapPost("/{id:guid}/cancel", CancelAsync).RequireAuthorization();

        api.MapGet("/availability", AvailabilityAsync).WithTags("Reservations").AllowAnonymous();
    }

    private static async Task<IResult> CreateAsync(
        ReservationRequest r, TableReservationDbContext db, ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        var isCustomer = user.IsInRole(AicosRoles.Customer);
        if (!isCustomer && !IsStaff(user))
        {
            throw new ForbiddenException("Only customers and branch staff can create reservations.");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        Validate(r, now, isCustomer);
        var branchId = isCustomer ? r.BranchId!.Value : BranchAccess.Resolve(user, r.BranchId);
        var duration = r.DurationMinutes ?? DefaultDurationMinutes;
        var start = r.ReservedAt!.Value.UtcDateTime;

        if (r.TableId is not null)
        {
            await EnsureTableBookableAsync(db, r.TableId.Value, branchId, r.PartySize!.Value, start, start.AddMinutes(duration), null, ct);
        }

        // Staff booking a specific table is confirmed straight away; everything else waits for staff confirmation.
        var status = !isCustomer && r.TableId is not null ? ReservationStatus.Confirmed : ReservationStatus.Pending;
        var reservation = new Reservation(
            branchId, r.TableId, isCustomer ? user.UserId : null, r.CustomerName!, r.CustomerPhone!, r.PartySize!.Value,
            start, duration, r.Note, status);
        db.Reservations.Add(reservation);
        await db.SaveChangesAsync(ct);

        return Results.CreatedAtRoute("GetReservation", new { id = reservation.Id }, await MapAsync(db, reservation, ct));
    }

    private static async Task<PagedResponse<ReservationResponse>> ListAsync(
        TableReservationDbContext db, ICurrentUser user, Guid? branchId, DateTimeOffset? from, DateTimeOffset? to,
        ReservationStatus? status, Guid? tableId, int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        var branch = BranchAccess.ResolveFilter(user, branchId);
        var query = db.Reservations.AsNoTracking();
        if (branch is not null)
        {
            query = query.Where(x => x.BranchId == branch);
        }

        if (from is not null)
        {
            var fromUtc = from.Value.UtcDateTime;
            query = query.Where(x => x.ReservedAtUtc >= fromUtc);
        }

        if (to is not null)
        {
            var toUtc = to.Value.UtcDateTime;
            query = query.Where(x => x.ReservedAtUtc < toUtc);
        }

        if (status is not null)
        {
            query = query.Where(x => x.Status == status);
        }

        if (tableId is not null)
        {
            query = query.Where(x => x.TableId == tableId);
        }

        var paged = await query.OrderBy(x => x.ReservedAtUtc).ToPagedAsync(page, pageSize, x => x, ct);
        return new PagedResponse<ReservationResponse>(await MapManyAsync(db, paged.Items, ct), paged.Page, paged.PageSize, paged.TotalCount);
    }

    private static async Task<List<ReservationResponse>> MineAsync(TableReservationDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var reservations = await db.Reservations.AsNoTracking()
            .Where(x => x.CustomerUserId == user.UserId)
            .OrderByDescending(x => x.ReservedAtUtc)
            .Take(50)
            .ToListAsync(ct);
        return await MapManyAsync(db, reservations, ct);
    }

    private static async Task<ReservationResponse> GetAsync(Guid id, TableReservationDbContext db, ICurrentUser user, CancellationToken ct) =>
        await MapAsync(db, await FindForReadAsync(db, user, id, ct), ct);

    private static async Task<ReservationResponse> ConfirmAsync(
        Guid id, ConfirmReservationRequest r, TableReservationDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var reservation = await FindForStaffAsync(db, user, id, ct);
        var tableId = r.TableId ?? reservation.TableId
            ?? throw new ValidationFailedException("TableId", "Choose a table to confirm this reservation.");

        await EnsureTableBookableAsync(db, tableId, reservation.BranchId, reservation.PartySize, reservation.ReservedAtUtc, reservation.EndsAtUtc, reservation.Id, ct);
        reservation.Confirm(tableId);
        await db.SaveChangesAsync(ct);
        return await MapAsync(db, reservation, ct);
    }

    private static async Task<ReservationResponse> SeatAsync(Guid id, TableReservationDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var reservation = await FindForStaffAsync(db, user, id, ct);
        reservation.Seat();
        var table = await db.DiningTables.FirstAsync(t => t.Id == reservation.TableId, ct);
        table.ChangeStatus(TableStatus.Occupied);
        await db.SaveChangesAsync(ct);
        return await MapAsync(db, reservation, ct);
    }

    private static async Task<ReservationResponse> CompleteAsync(Guid id, TableReservationDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var reservation = await FindForStaffAsync(db, user, id, ct);
        reservation.Complete();
        var table = await db.DiningTables.FirstAsync(t => t.Id == reservation.TableId, ct);
        table.ChangeStatus(TableStatus.Available);
        await db.SaveChangesAsync(ct);
        return await MapAsync(db, reservation, ct);
    }

    private static async Task<ReservationResponse> NoShowAsync(
        Guid id, TableReservationDbContext db, ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        var reservation = await FindForStaffAsync(db, user, id, ct);
        if (clock.GetUtcNow().UtcDateTime < reservation.ReservedAtUtc)
        {
            throw new BusinessRuleException("reservation.too_early", "A reservation can only be marked as no-show after its start time.");
        }

        reservation.MarkNoShow();
        await db.SaveChangesAsync(ct);
        return await MapAsync(db, reservation, ct);
    }

    /// <summary>The customer who made it (before the start time) or branch staff (any time) can cancel.</summary>
    private static async Task<ReservationResponse> CancelAsync(
        Guid id, CancelReservationRequest r, TableReservationDbContext db, ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        var reservation = await FindForReadAsync(db, user, id, ct);
        if (!IsStaff(user) && clock.GetUtcNow().UtcDateTime >= reservation.ReservedAtUtc)
        {
            throw new BusinessRuleException("reservation.too_late", "The reservation has already started; please contact the shop.");
        }

        var errors = new ValidationErrors();
        CommonRules.MaxLength(errors, "Reason", r.Reason, "Reason", 500);
        errors.ThrowIfAny();

        reservation.Cancel(r.Reason);
        await db.SaveChangesAsync(ct);
        return await MapAsync(db, reservation, ct);
    }

    /// <summary>Public: tables of a branch free for the whole slot and big enough for the party.</summary>
    private static async Task<List<AvailableTableResponse>> AvailabilityAsync(
        TableReservationDbContext db, Guid branchId, DateTimeOffset at, int? partySize, int? durationMinutes, CancellationToken ct)
    {
        var start = at.UtcDateTime;
        var end = start.AddMinutes(durationMinutes is >= 30 and <= 240 ? durationMinutes.Value : DefaultDurationMinutes);
        var size = partySize is >= 1 ? partySize.Value : 1;

        var busy = db.Reservations
            .Where(x => x.BranchId == branchId && x.TableId != null && Reservation.ActiveStatuses.Contains(x.Status)
                        && x.ReservedAtUtc < end && start < x.EndsAtUtc)
            .Select(x => x.TableId!.Value);

        var tables = await db.DiningTables.AsNoTracking()
            .Where(t => t.BranchId == branchId && t.Status != TableStatus.OutOfService && t.Capacity >= size && !busy.Contains(t.Id))
            .OrderBy(t => t.Capacity).ThenBy(t => t.TableCode)
            .ToListAsync(ct);
        return tables.Select(t => new AvailableTableResponse(t.Id, t.TableCode, t.Capacity, t.Area)).ToList();
    }

    private static void Validate(ReservationRequest r, DateTime nowUtc, bool isCustomer)
    {
        var errors = new ValidationErrors();
        if (isCustomer)
        {
            CommonRules.RequiredId(errors, "BranchId", r.BranchId, "Branch");
        }

        CommonRules.Required(errors, "CustomerName", r.CustomerName, "Customer name", 100);
        CommonRules.Required(errors, "CustomerPhone", r.CustomerPhone, "Customer phone", 20);
        CommonRules.Phone(errors, "CustomerPhone", r.CustomerPhone);
        CommonRules.Range(errors, "PartySize", r.PartySize, "Party size", 1, 50);
        if (r.DurationMinutes is not null)
        {
            CommonRules.Range(errors, "DurationMinutes", r.DurationMinutes, "Duration (minutes)", 30, 240);
        }

        CommonRules.MaxLength(errors, "Note", r.Note, "Note", 500);

        if (r.ReservedAt is null)
        {
            errors.Add("ReservedAt", "Reservation time is required.");
        }
        else
        {
            var start = r.ReservedAt.Value.UtcDateTime;
            // Customers book at least 15 minutes ahead; staff may register a walk-in that starts now.
            var earliest = isCustomer ? nowUtc.AddMinutes(MinLeadMinutes) : nowUtc.AddMinutes(-5);
            if (start < earliest)
            {
                errors.Add("ReservedAt", isCustomer
                    ? $"Reservations must be made at least {MinLeadMinutes} minutes in advance."
                    : "Reservation time cannot be in the past.");
            }
            else if (start > nowUtc.AddDays(MaxDaysAhead))
            {
                errors.Add("ReservedAt", $"Reservations can be made at most {MaxDaysAhead} days ahead.");
            }
        }

        errors.ThrowIfAny();
    }

    /// <summary>Table belongs to the branch, is in service, is big enough and is free for the whole slot (BR-10).</summary>
    private static async Task EnsureTableBookableAsync(
        TableReservationDbContext db, Guid tableId, Guid branchId, int partySize, DateTime start, DateTime end, Guid? excludeReservationId, CancellationToken ct)
    {
        var table = await db.DiningTables.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tableId, ct);
        if (table is null || table.BranchId != branchId)
        {
            throw new ValidationFailedException("TableId", "Table does not exist in this branch.");
        }

        if (table.Status == TableStatus.OutOfService)
        {
            throw new ValidationFailedException("TableId", $"Table {table.TableCode} is out of service.");
        }

        if (partySize > table.Capacity)
        {
            throw new ValidationFailedException("PartySize", $"Table {table.TableCode} seats at most {table.Capacity} guests.");
        }

        var overlapping = await db.Reservations.AnyAsync(x =>
            x.TableId == tableId && x.Id != excludeReservationId && Reservation.ActiveStatuses.Contains(x.Status)
            && x.ReservedAtUtc < end && start < x.EndsAtUtc, ct);
        if (overlapping)
        {
            throw new ConflictException($"BR-10: table {table.TableCode} is already reserved for an overlapping time slot.", "reservation.table_unavailable");
        }
    }

    private static bool IsStaff(ICurrentUser user) =>
        user.IsInRole(AicosRoles.SystemAdmin) || user.IsInRole(AicosRoles.Manager) || user.IsInRole(AicosRoles.Cashier);

    private static async Task<Reservation> FindForStaffAsync(TableReservationDbContext db, ICurrentUser user, Guid id, CancellationToken ct)
    {
        var reservation = await db.Reservations.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Reservation", id);
        BranchAccess.EnsureCanAccess(user, reservation.BranchId);
        return reservation;
    }

    /// <summary>Staff of the branch, or the customer who owns the reservation.</summary>
    private static async Task<Reservation> FindForReadAsync(TableReservationDbContext db, ICurrentUser user, Guid id, CancellationToken ct)
    {
        var reservation = await db.Reservations.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Reservation", id);
        if (IsStaff(user))
        {
            BranchAccess.EnsureCanAccess(user, reservation.BranchId);
        }
        else if (reservation.CustomerUserId is null || reservation.CustomerUserId != user.UserId)
        {
            throw new NotFoundException("Reservation", id); // do not reveal other customers' bookings
        }

        return reservation;
    }

    private static async Task<ReservationResponse> MapAsync(TableReservationDbContext db, Reservation x, CancellationToken ct) =>
        (await MapManyAsync(db, [x], ct))[0];

    private static async Task<List<ReservationResponse>> MapManyAsync(TableReservationDbContext db, IReadOnlyList<Reservation> items, CancellationToken ct)
    {
        var tableIds = items.Where(x => x.TableId != null).Select(x => x.TableId!.Value).Distinct().ToList();
        var codes = await db.DiningTables.AsNoTracking().Where(t => tableIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.TableCode, ct);
        return items.Select(x => new ReservationResponse(
            x.Id, x.BranchId, x.TableId, x.TableId is null ? null : codes.GetValueOrDefault(x.TableId.Value), x.CustomerUserId,
            x.CustomerName, x.CustomerPhone, x.PartySize, x.ReservedAtUtc, x.EndsAtUtc, x.DurationMinutes, x.Status, x.Note,
            x.CancelReason, x.CreatedAtUtc)).ToList();
    }
}
