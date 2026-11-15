namespace AICOS.Contracts.Billing;

/// <summary>Confirmed money, not net sales. EventId is the stable payment attempt identity.</summary>
public sealed record CollectionReceipt(Guid EventId, Guid TenantId, Guid BranchId, Guid? BillId, Guid? OrderId,
    Guid? CashierId, Guid? ShiftId, string Method, decimal Amount, DateTime PaidAtUtc, string Reference, bool Recovered = false);
