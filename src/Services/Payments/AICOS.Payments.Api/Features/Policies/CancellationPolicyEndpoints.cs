using AICOS.BuildingBlocks.Messaging;
using AICOS.BuildingBlocks.Security;
using AICOS.BuildingBlocks.Validation;
using AICOS.Payments.Api.Domain;
using AICOS.Payments.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.Payments.Api.Features.Policies;

/// <summary>PUT /policies/cancellation: the whole policy of the brand. Percentages are 0-100, times are minutes.</summary>
public sealed record CancellationPolicyRequest(
    int? CancelRefundPercent,
    int? NoShowAfterMinutes,
    int? NoShowRefundPercent,
    int? UnpaidOrderTimeoutMinutes,
    bool? AutoApproveWithinPolicy,
    RefundApprover? ExceptionApprover);

/// <summary><see cref="IsDefault"/> is true while the brand has not configured its own policy and the platform defaults apply.</summary>
public sealed record CancellationPolicyResponse(
    int CancelRefundPercent,
    int NoShowAfterMinutes,
    int NoShowRefundPercent,
    int UnpaidOrderTimeoutMinutes,
    bool AutoApproveWithinPolicy,
    RefundApprover ExceptionApprover,
    bool IsDefault)
{
    public static CancellationPolicyResponse From(EffectiveCancellationPolicy p) => new(
        p.CancelRefundPercent, p.NoShowAfterMinutes, p.NoShowRefundPercent, p.UnpaidOrderTimeoutMinutes, p.AutoApproveWithinPolicy, p.ExceptionApprover, p.IsDefault);
}

/// <summary>
/// UC-18 Configure Cancellation and Refund Policy. Routes: /api/v1/payments/policies/cancellation. Read: every signed-in user of the
/// brand (customers see the terms they order under). Write: BrandAdmin only. Changes are sent to the audit history.
/// </summary>
public static class CancellationPolicyEndpoints
{
    public static void MapCancellationPolicyEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/policies/cancellation").WithTags("Cancellation policy");

        group.MapGet("/", GetAsync).RequireAuthorization();
        group.MapPut("/", UpdateAsync).RequireAuthorization(AicosPolicies.AdminOnly);
    }

    private static async Task<CancellationPolicyResponse> GetAsync(PaymentsDbContext db, ICurrentUser user, CancellationToken ct) =>
        CancellationPolicyResponse.From(await ForTenantAsync(db, TenantScope.Require(user), ct));

    private static async Task<CancellationPolicyResponse> UpdateAsync(
        CancellationPolicyRequest r, PaymentsDbContext db, ICurrentUser user, IEventBus bus, ILoggerFactory loggers, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        if (r.CancelRefundPercent is null or < 0 or > 100)
        {
            errors.Add("CancelRefundPercent", "Refund percentage must be between 0 and 100.");
        }

        if (r.NoShowRefundPercent is null or < 0 or > 100)
        {
            errors.Add("NoShowRefundPercent", "Refund percentage must be between 0 and 100.");
        }

        if (r.NoShowAfterMinutes is null or < 5 or > 240)
        {
            errors.Add("NoShowAfterMinutes", "A no-show is declared after 5-240 minutes.");
        }

        if (r.UnpaidOrderTimeoutMinutes is null or < 5 or > 120)
        {
            errors.Add("UnpaidOrderTimeoutMinutes", "An unpaid order is released after 5-120 minutes.");
        }

        if (r.AutoApproveWithinPolicy is null)
        {
            errors.Add("AutoApproveWithinPolicy", "AutoApproveWithinPolicy is required (true or false).");
        }

        CommonRules.RequiredEnum(errors, "ExceptionApprover", r.ExceptionApprover, "Exception approver");
        errors.ThrowIfAny();

        var tenantId = TenantScope.Require(user);
        var before = await ForTenantAsync(db, tenantId, ct);
        var values = new EffectiveCancellationPolicy(
            r.CancelRefundPercent!.Value, r.NoShowAfterMinutes!.Value, r.NoShowRefundPercent!.Value, r.UnpaidOrderTimeoutMinutes!.Value,
            r.AutoApproveWithinPolicy!.Value, r.ExceptionApprover!.Value, IsDefault: false);

        var policy = await db.CancellationPolicies.FirstOrDefaultAsync(p => p.TenantId == tenantId, ct);
        if (policy is null)
        {
            db.CancellationPolicies.Add(new CancellationPolicy(tenantId, values));
        }
        else
        {
            policy.Apply(values);
        }

        await db.SaveChangesAsync(ct);
        await AuditTrail.PublishAsync(
            bus, user, loggers.CreateLogger("AICOS.Payments.Policy"), tenantId, "cancellation_policy.updated", "CancellationPolicy", tenantId, Snapshot(before), Snapshot(values));
        return CancellationPolicyResponse.From(values);
    }

    /// <summary>The policy in force for a tenant; other services use it to decide whether a refund is automatic or an exception.</summary>
    internal static async Task<EffectiveCancellationPolicy> ForTenantAsync(PaymentsDbContext db, Guid tenantId, CancellationToken ct)
    {
        var own = await db.CancellationPolicies.AsNoTracking().FirstOrDefaultAsync(p => p.TenantId == tenantId, ct);
        return own?.ToValues() ?? EffectiveCancellationPolicy.Defaults;
    }

    private static object Snapshot(EffectiveCancellationPolicy p) => new
    {
        p.CancelRefundPercent, p.NoShowAfterMinutes, p.NoShowRefundPercent, p.UnpaidOrderTimeoutMinutes, p.AutoApproveWithinPolicy,
        ExceptionApprover = p.ExceptionApprover.ToString(), p.IsDefault,
    };
}
