namespace AICOS.Contracts.Events;

/// <summary>
/// Published by Menu Service whenever what a branch can sell changes: a menu was published, a price changed, or a product
/// became available / sold out (NFR-25). QR ordering, counter POS and takeaway use it to refresh. <see cref="BranchId"/> is
/// null when the change reaches every branch of the tenant (for example a product was edited).
/// </summary>
public sealed record MenuChangedEvent(Guid TenantId, Guid? BranchId, string Reason) : IntegrationEvent;
