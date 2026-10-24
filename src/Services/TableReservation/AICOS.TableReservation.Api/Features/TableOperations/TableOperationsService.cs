using System.Data;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.TableReservation.Api.Domain;
using AICOS.TableReservation.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AICOS.TableReservation.Api.Features.TableOperations;

public sealed class TableOperationsService(
    TableReservationDbContext dbContext,
    TimeProvider clock,
    ILogger<TableOperationsService> logger)
{
    public async Task<TableSessionResponse> GetSessionAsync(
        Guid sessionId,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ValidationFailedException("sessionId", "Session is required.");
        }

        var branchId = RequireBranch(currentUser);
        var session = await dbContext.TableSessions
            .AsNoTracking()
            .Include(item => item.TableLinks)
            .SingleOrDefaultAsync(
                item => item.Id == sessionId && item.BranchId == branchId,
                cancellationToken)
            ?? throw new NotFoundException("Table session", sessionId);

        return ToSessionResponse(
            session,
            session.TableLinks
                .Where(link => link.LeftAt is null)
                .Select(link => link.TableId)
                .ToList());
    }

    public async Task<TableMapResponse> GetMapAsync(
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        var branchId = RequireBranch(currentUser);
        var areas = await dbContext.TableAreas
            .AsNoTracking()
            .Include(area => area.Tables)
            .Where(area => area.BranchId == branchId)
            .OrderBy(area => area.SortOrder)
            .ThenBy(area => area.Name)
            .ToListAsync(cancellationToken);

        var tableIds = areas.SelectMany(area => area.Tables).Select(table => table.Id).ToList();
        var activeLinks = await dbContext.TableSessionTables
            .AsNoTracking()
            .Include(link => link.Session)
            .Where(link =>
                tableIds.Contains(link.TableId) &&
                link.LeftAt == null &&
                link.Session.Status == TableSessionStatuses.Open)
            .OrderByDescending(link => link.Session.OpenedAt)
            .ToListAsync(cancellationToken);
        var activeSessions = activeLinks
            .GroupBy(link => link.TableId)
            .ToDictionary(group => group.Key, group => group.First().Session);

        return new TableMapResponse(
            branchId,
            areas.Select(area => new TableAreaResponse(
                area.Id,
                area.Name,
                area.SortOrder ?? 0,
                area.Tables
                    .OrderBy(table => table.TableNumber)
                    .Select(table => ToTableResponse(
                        table,
                        activeSessions.GetValueOrDefault(table.Id)))
                    .ToList()))
                .ToList());
    }

    public Task<TableSessionResponse> OpenSessionAsync(
        OpenTableSessionRequest request,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        if (request.TableId == Guid.Empty)
        {
            throw new ValidationFailedException("tableId", "Table is required.");
        }

        var branchId = RequireBranch(currentUser);
        return ExecuteSerializableAsync(async () =>
        {
            var table = await dbContext.CafeTables
                .Include(item => item.Area)
                .SingleOrDefaultAsync(
                    item => item.Id == request.TableId && item.Area.BranchId == branchId,
                    cancellationToken)
                ?? throw new NotFoundException("Table", request.TableId);

            var hasActiveSession = await dbContext.TableSessionTables.AnyAsync(
                link => link.TableId == table.Id &&
                    link.LeftAt == null &&
                    link.Session.Status == TableSessionStatuses.Open,
                cancellationToken);
            if (table.Status != CafeTableStatuses.Available || hasActiveSession)
            {
                throw new ConflictException(
                    "The table is not available for a new session.",
                    "table.not_available");
            }

            var now = LocalNow();
            var session = new TableSession(Guid.NewGuid(), branchId, now);
            var link = new TableSessionTable(Guid.NewGuid(), session.Id, table.Id, now);
            table.MarkOccupied();
            dbContext.AddRange(session, link);
            await dbContext.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "User {UserId} opened session {SessionId} for table {TableId} at branch {BranchId}",
                currentUser.UserId,
                session.Id,
                table.Id,
                branchId);
            return ToSessionResponse(session, [table.Id]);
        }, cancellationToken);
    }

    public Task<TableSessionResponse> CloseSessionAsync(
        Guid sessionId,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ValidationFailedException("sessionId", "Session is required.");
        }

        var branchId = RequireBranch(currentUser);
        return ExecuteSerializableAsync(async () =>
        {
            var session = await dbContext.TableSessions
                .Include(item => item.TableLinks.Where(link => link.LeftAt == null))
                    .ThenInclude(link => link.Table)
                .SingleOrDefaultAsync(
                    item => item.Id == sessionId && item.BranchId == branchId,
                    cancellationToken)
                ?? throw new NotFoundException("Table session", sessionId);

            if (session.Status != TableSessionStatuses.Open)
            {
                throw new ConflictException(
                    "The table session is already closed.",
                    "table.session_closed");
            }

            var activeLinks = session.TableLinks.Where(link => link.LeftAt is null).ToList();
            if (activeLinks.Count == 0)
            {
                throw new ConflictException(
                    "The table session has no active table.",
                    "table.session_has_no_active_table");
            }

            var now = LocalNow();
            foreach (var link in activeLinks)
            {
                link.Leave(now);
                link.Table.MarkCleaning();
            }

            session.Close(now);
            await dbContext.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "User {UserId} closed session {SessionId} at branch {BranchId}",
                currentUser.UserId,
                session.Id,
                branchId);
            return ToSessionResponse(session, activeLinks.Select(link => link.TableId).ToList());
        }, cancellationToken);
    }

    public Task<CafeTableResponse> MarkCleanedAsync(
        Guid tableId,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        if (tableId == Guid.Empty)
        {
            throw new ValidationFailedException("tableId", "Table is required.");
        }

        var branchId = RequireBranch(currentUser);
        return ExecuteSerializableAsync(async () =>
        {
            var table = await dbContext.CafeTables
                .Include(item => item.Area)
                .SingleOrDefaultAsync(
                    item => item.Id == tableId && item.Area.BranchId == branchId,
                    cancellationToken)
                ?? throw new NotFoundException("Table", tableId);

            var hasActiveSession = await dbContext.TableSessionTables.AnyAsync(
                link => link.TableId == table.Id &&
                    link.LeftAt == null &&
                    link.Session.Status == TableSessionStatuses.Open,
                cancellationToken);
            if (table.Status != CafeTableStatuses.Cleaning || hasActiveSession)
            {
                throw new ConflictException(
                    "Only a table waiting for cleaning can be marked available.",
                    "table.not_waiting_for_cleaning");
            }

            table.MarkAvailable();
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "User {UserId} marked table {TableId} available at branch {BranchId}",
                currentUser.UserId,
                table.Id,
                branchId);
            return ToTableResponse(table, null);
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
                    "The table changed while the request was being processed. Reload and try again.",
                    "table.concurrent_update");
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

    private static CafeTableResponse ToTableResponse(CafeTable table, TableSession? session) =>
        new(
            table.Id,
            table.TableNumber,
            table.Capacity ?? 2,
            table.Status ?? CafeTableStatuses.Available,
            session is null
                ? null
                : new ActiveTableSessionResponse(
                    session.Id,
                    session.Status ?? TableSessionStatuses.Open,
                    session.OpenedAt));

    private static TableSessionResponse ToSessionResponse(
        TableSession session,
        IReadOnlyList<Guid> tableIds) =>
        new(
            session.Id,
            session.BranchId,
            session.Status ?? TableSessionStatuses.Open,
            session.OpenedAt,
            session.ClosedAt,
            tableIds);
}
