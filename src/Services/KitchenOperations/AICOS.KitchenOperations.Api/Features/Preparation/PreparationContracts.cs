namespace AICOS.KitchenOperations.Api.Features.Preparation;

public sealed record PreparationQueueResponse(
    Guid BranchId,
    IReadOnlyList<PreparationOrderResponse> Orders);

public sealed record PreparationOrderResponse(
    Guid Id,
    string OrderCode,
    string OrderType,
    DateTime? CreatedAt,
    IReadOnlyList<PreparationItemResponse> Items);

public sealed record PreparationItemResponse(
    Guid Id,
    string ProductName,
    string? VariantName,
    int Quantity,
    string Status,
    string? Note);

public sealed record UpdatePreparationStatusRequest(string Status);
