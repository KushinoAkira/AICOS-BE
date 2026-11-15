using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.Payments.Api.Features.Payments;
using AICOS.Payments.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.Payments.Api.Features.CashierShifts;

public interface ICashActorVerifier
{
    Task EnsureAsync(ICurrentUser user, CancellationToken ct);
}

// Re-read the current account, not the stale role/branch embedded in an otherwise valid JWT.
public sealed class HttpCashActorVerifier(HttpClient http) : ICashActorVerifier
{
    public async Task EnsureAsync(ICurrentUser user, CancellationToken ct)
    {
        HttpResponseMessage response;
        try { response = await http.GetAsync("/api/v1/identity/auth/me", ct); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        { throw new DependencyUnavailableException("identity-cash-authorization"); }
        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                throw new ForbiddenException("Account authorization has changed. Sign in again.", "cashier.actor_changed");
            if (!response.IsSuccessStatusCode) throw new DependencyUnavailableException("identity-cash-authorization");
            JsonElement me;
            try { me = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct); }
            catch (JsonException) { throw new DependencyUnavailableException("identity-cash-authorization"); }
            Guid? Id(string key) => me.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && Guid.TryParse(v.GetString(), out var id) ? id : null;
            var role = me.TryGetProperty("role", out var r) ? r.GetString() : null;
            if (Id("id") != user.UserId || Id("tenantId") != user.TenantId || Id("branchId") != user.BranchId
                || role is null || !PaymentAccess.CounterRoles.Contains(role) || !user.IsInRole(role)
                || !me.TryGetProperty("status", out var status) || status.GetString() != "Active"
                || !me.TryGetProperty("mustChangePassword", out var change) || change.ValueKind != JsonValueKind.False
                || me.TryGetProperty("lockoutEndUtc", out var locked) && locked.ValueKind == JsonValueKind.String
                   && DateTimeOffset.TryParse(locked.GetString(), out var until) && until > DateTimeOffset.UtcNow)
                throw new ForbiddenException("Account authorization has changed. Sign in again.", "cashier.actor_changed");
        }
    }
}

public sealed class CashActorFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var services = context.HttpContext.RequestServices;
        await services.GetRequiredService<ICashActorVerifier>().EnsureAsync(services.GetRequiredService<ICurrentUser>(), context.HttpContext.RequestAborted);
        return await next(context);
    }
}

public static class CashPrecheck
{
    public static async Task<object> GetAsync(Guid? branchId, PaymentsDbContext db, ICurrentUser user,
        IBranchDirectory directory, ICashActorVerifier verifier, CancellationToken ct)
    {
        var tenant = TenantScope.Require(user);
        var actor = user.UserId ?? throw new UnauthorizedException("Sign in first.");
        var branch = await BranchScope.ResolveAsync(user, directory, branchId, false, ct);
        await verifier.EnsureAsync(user, ct);
        var required = db.RequireCashierShift;
        var status = "NOT_REQUIRED";
        var allowed = !required;
        if (required)
        {
            if (!db.CashierEnabled) status = "UNAVAILABLE";
            else
            {
                var shift = await db.CashierShifts.AsNoTracking().Where(s => s.TenantId == tenant && s.BranchId == branch && s.CashierId == actor)
                    .OrderBy(s => s.ClosedAtUtc != null).ThenByDescending(s => s.OpenedAtUtc).FirstOrDefaultAsync(ct);
                status = shift is null ? "MISSING" : shift.ClosedAtUtc is null ? "OPEN" : "CLOSED";
                allowed = status == "OPEN";
            }
        }
        var reason = allowed ? "cashier.allowed" : status == "UNAVAILABLE" ? "cashier.shift_unavailable" : "cashier.open_shift_required";
        return new { BranchId = branch, CashierId = actor, Allowed = allowed, RequiresOpenShift = required, ShiftStatus = status, ReasonCode = reason,
            Message = allowed ? "Có thể tiếp tục kiểm tra hóa đơn để thu tiền mặt." : status == "UNAVAILABLE" ? "Backend chưa sẵn sàng kiểm tra ca thu ngân." : "Hãy mở ca thu ngân của bạn tại chi nhánh này trước khi thu tiền mặt." };
    }
}
