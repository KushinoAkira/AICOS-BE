namespace AICOS.Contracts.Events;

/// <summary>A cashier counted the cash of a shift. Customer Engagement tells the managers of the branch that it waits for them (UC-33).</summary>
public sealed record CashClosingSubmittedEvent(
    Guid TenantId, Guid BranchId, Guid ClosingId, string EmployeeName, decimal ExpectedAmount, decimal CountedAmount) : IntegrationEvent;

/// <summary>A manager opened a replacement shift. Customer Engagement tells the staff of the branch that they can register.</summary>
public sealed record ReplacementShiftOpenedEvent(
    Guid TenantId, Guid BranchId, Guid ReplacementId, DateOnly WorkDate, TimeOnly StartTime, TimeOnly EndTime) : IntegrationEvent;

/// <summary>A staff member registered for a replacement shift. Customer Engagement tells the managers of the branch.</summary>
public sealed record ReplacementRegisteredEvent(
    Guid TenantId, Guid BranchId, Guid ReplacementId, Guid EmployeeId, string EmployeeName, DateOnly WorkDate) : IntegrationEvent;
