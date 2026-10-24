namespace AICOS.TableReservation.Api.Features.TableOperations;

public sealed record TableMapResponse(
    Guid BranchId,
    IReadOnlyList<TableAreaResponse> Areas);

public sealed record TableAreaResponse(
    Guid Id,
    string Name,
    int SortOrder,
    IReadOnlyList<CafeTableResponse> Tables);

public sealed record CafeTableResponse(
    Guid Id,
    string TableNumber,
    int Capacity,
    string Status,
    ActiveTableSessionResponse? ActiveSession);

public sealed record ActiveTableSessionResponse(
    Guid Id,
    string Status,
    DateTime? OpenedAt);

public sealed record OpenTableSessionRequest(Guid TableId);

public sealed record TableSessionResponse(
    Guid Id,
    Guid BranchId,
    string Status,
    DateTime? OpenedAt,
    DateTime? ClosedAt,
    IReadOnlyList<Guid> TableIds);
