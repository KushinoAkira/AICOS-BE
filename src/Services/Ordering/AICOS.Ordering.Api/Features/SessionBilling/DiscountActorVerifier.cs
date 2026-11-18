using System.Net;
using System.Text.Json;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.Ordering.Api.Features.Orders;

namespace AICOS.Ordering.Api.Features.SessionBilling;

public interface IDiscountActorVerifier
{
    Task EnsureAsync(ICurrentUser user, CancellationToken ct);
}

public sealed class DiscountActorVerifier(HttpClient http, TimeProvider clock) : IDiscountActorVerifier
{
    public async Task EnsureAsync(ICurrentUser user, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync("/api/v1/identity/auth/me", ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                throw new ForbiddenException("Account authorization changed.", "discount.actor_changed");
            if (!response.IsSuccessStatusCode) throw new DependencyUnavailableException("identity-discount-authorization");
            var me = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            if (!Valid(me, user, clock.GetUtcNow()))
                throw new ForbiddenException("Account authorization changed.", "discount.actor_changed");
        }
        catch (Exception e) when (e is HttpRequestException or JsonException || e is TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new DependencyUnavailableException("identity-discount-authorization");
        }
    }

    public static bool Valid(JsonElement me, ICurrentUser user, DateTimeOffset now)
    {
        if (me.ValueKind != JsonValueKind.Object) return false;
        Guid? Id(string key) => me.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            && Guid.TryParse(v.GetString(), out var id) ? id : null;
        string? Text(string key) => me.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var roles = new HashSet<string>();
        if (Text("role") is { } primary) roles.Add(primary);
        if (me.TryGetProperty("roles", out var values) && values.ValueKind == JsonValueKind.Array)
            foreach (var value in values.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)) roles.Add(value.GetString()!);
        return user.UserId is not null && Id("id") == user.UserId && Id("tenantId") == user.TenantId
            && Id("branchId") == user.BranchId && Text("status") == "Active"
            && me.TryGetProperty("mustChangePassword", out var change) && change.ValueKind == JsonValueKind.False
            && OrderAccess.CounterRoles.Any(role => roles.Contains(role) && user.IsInRole(role))
            && (Text("lockoutEndUtc") is not { } locked || DateTimeOffset.TryParse(locked, out var until) && until <= now);
    }
}
