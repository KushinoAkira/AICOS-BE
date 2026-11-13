using AICOS.Ordering.Api.Domain;
using AICOS.Ordering.Api.Domain.Enums;
using AICOS.Ordering.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.Ordering.Api.Features.Qr;

/// <summary>Advisory snapshot only. POST close always rechecks under the table lock.</summary>
public sealed record SessionCloseEligibility(Guid SessionId, Guid BranchId, bool CanClose,
    int UnsettledOrders, int UnservedOrders, bool RequiresBillVerification, IReadOnlyList<string> Reasons,
    bool AdvisoryOnly = true)
{
    public static async Task<SessionCloseEligibility> ReadAsync(OrderingDbContext db, TableSession session, CancellationToken ct)
    {
        var rows = await db.Orders.AsNoTracking().Where(o => o.TableSessionId == session.Id)
            .Select(o => new { o.Status, o.PaymentStatus, o.DeliveredAtUtc }).ToListAsync(ct);
        var live = rows.Where(o => o.Status is not (OrderStatus.Cancelled or OrderStatus.NoShow or OrderStatus.Unfulfillable)).ToList();
        var unsettled = live.Count(o => o.PaymentStatus is not (PaymentStatus.Paid or PaymentStatus.Refunded));
        // This counts missing delivery proof; it cannot prove CupV1 custody is clear.
        var unserved = live.Count(o => o.DeliveredAtUtc is null);
        var reasons = new List<string>();
        if (!session.IsOpen) reasons.Add("qr.session_closed");
        if (unsettled > 0) reasons.Add("qr.session_unsettled");
        if (unserved > 0) reasons.Add("qr.session_unserved");
        if (rows.Count > 0) reasons.Add("qr.session_verification_required");
        return new(session.Id, session.BranchId, reasons.Count == 0, unsettled, unserved, rows.Count > 0, reasons);
    }
}
