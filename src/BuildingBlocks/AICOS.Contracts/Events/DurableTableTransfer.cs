namespace AICOS.Contracts.Events;
public sealed record DurableTableTransfer(Guid OperationId,Guid TenantId,Guid BranchId,Guid SessionId,long Version,Guid SourceTableId,Guid TargetTableId,string TargetCode,IReadOnlyList<Guid> OrderIds);
public sealed record TableTransferAck(Guid OperationId,long Version,bool Applied);
