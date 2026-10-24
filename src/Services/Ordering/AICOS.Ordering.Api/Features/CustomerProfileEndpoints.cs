using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.Ordering.Api.Services.Takeaway;
using Microsoft.AspNetCore.Mvc;

namespace AICOS.Ordering.Api.Features;

public static class CustomerProfileEndpoints
{
    public static void MapCustomerProfileEndpoints(this RouteGroupBuilder api)
    {
        var me = api.MapGroup("/")
            .WithTags("Customer")
            .RequireAuthorization(AicosPolicies.Customer);

        me.MapGet("/me", async ([FromServices] CustomerProfileService profiles, [FromServices] ICurrentUser user, CancellationToken ct) =>
        {
            var (id, tid) = Require(user);
            return Results.Ok(await profiles.GetMeAsync(id, tid, ct));
        });

        me.MapPatch("/me", async (
            [FromBody] PatchMeRequest body,
            [FromServices] CustomerProfileService profiles,
            [FromServices] ICurrentUser user,
            CancellationToken ct) =>
        {
            var (id, tid) = Require(user);
            return Results.Ok(await profiles.PatchMeAsync(id, tid, body, ct));
        });

        me.MapPut("/me/preferences", async (
            [FromBody] UpsertPreferencesRequest body,
            [FromServices] CustomerProfileService profiles,
            [FromServices] ICurrentUser user,
            CancellationToken ct) =>
        {
            var (id, _) = Require(user);
            await profiles.UpsertPreferencesAsync(id, body, ct);
            return Results.NoContent();
        });

        me.MapGet("/branches", async ([FromServices] CustomerProfileService profiles, [FromServices] ICurrentUser user, CancellationToken ct) =>
        {
            var (_, tid) = Require(user);
            return Results.Ok(await profiles.ListActiveBranchesAsync(tid, ct));
        });

        me.MapGet("/branches/nearest", async (
            [FromQuery] double lat,
            [FromQuery] double lng,
            [FromServices] CustomerProfileService profiles,
            [FromServices] ICurrentUser user,
            CancellationToken ct) =>
        {
            var (_, tid) = Require(user);
            return Results.Ok(await profiles.GetNearestBranchAsync(tid, lat, lng, ct));
        });

        me.MapPut("/me/preferred-branch", async (
            [FromBody] PreferredBranchRequest body,
            [FromServices] CustomerProfileService profiles,
            [FromServices] ICurrentUser user,
            CancellationToken ct) =>
        {
            var (id, tid) = Require(user);
            await profiles.SetPreferredBranchAsync(id, tid, body.BranchId, ct);
            return Results.NoContent();
        });

        me.MapGet("/me/points", async ([FromServices] CustomerProfileService profiles, [FromServices] ICurrentUser user, CancellationToken ct) =>
        {
            var (id, tid) = Require(user);
            return Results.Ok(await profiles.GetPointsAsync(id, tid, ct));
        });

        me.MapPost("/me/rewards/{id:guid}/redeem", async (
            Guid id,
            [FromServices] CustomerProfileService profiles,
            [FromServices] ICurrentUser user,
            CancellationToken ct) =>
        {
            var (userId, tid) = Require(user);
            var redemption = await profiles.RedeemAsync(userId, tid, id, ct);
            return Results.Ok(new { redemption.Id, redemption.Code, redemption.ExpiresAt, redemption.Status });
        });

        me.MapGet("/me/notifications", async (
            [FromQuery] bool? unread,
            [FromQuery] string? cursor,
            [FromServices] CustomerProfileService profiles,
            [FromServices] ICurrentUser user,
            CancellationToken ct) =>
        {
            var (id, _) = Require(user);
            var (items, next) = await profiles.ListNotificationsAsync(id, unread == true, cursor, 20, ct);
            return Results.Ok(new { items, nextCursor = next });
        });

        me.MapPost("/me/notifications/{id:guid}/read", async (
            Guid id,
            [FromServices] CustomerProfileService profiles,
            [FromServices] ICurrentUser user,
            CancellationToken ct) =>
        {
            var (userId, _) = Require(user);
            await profiles.MarkNotificationReadAsync(userId, id, ct);
            return Results.NoContent();
        });

        me.MapPost("/me/push-tokens", async (
            [FromBody] PushTokenRequest body,
            [FromServices] CustomerProfileService profiles,
            [FromServices] ICurrentUser user,
            CancellationToken ct) =>
        {
            var (id, _) = Require(user);
            await profiles.UpsertPushTokenAsync(id, body.Token, ct);
            return Results.NoContent();
        });

        me.MapGet("/me/feedbacks", async ([FromServices] CustomerProfileService profiles, [FromServices] ICurrentUser user, CancellationToken ct) =>
        {
            var (id, _) = Require(user);
            return Results.Ok(await profiles.GetMyFeedbacksAsync(id, ct));
        });

        api.MapGet("/admin/feedbacks", async (
            [FromQuery] Guid? branch_id,
            [FromQuery] int? rating,
            [FromQuery] DateTime? from,
            [FromQuery] DateTime? to,
            [FromServices] CustomerProfileService profiles,
            [FromServices] ICurrentUser user,
            CancellationToken ct) =>
        {
            if (user.UserId is not Guid id || user.TenantId is not Guid tid)
            {
                throw new ForbiddenException();
            }

            return Results.Ok(await profiles.ListAdminFeedbacksAsync(id, user.Role, tid, branch_id, rating, from, to, ct));
        }).RequireAuthorization(AicosPolicies.Management).WithTags("Customer");

        me.MapPost("/me/deletion-requests", async (
            [FromBody] DeletionRequestBody body,
            [FromServices] CustomerProfileService profiles,
            [FromServices] ICurrentUser user,
            CancellationToken ct) =>
        {
            var (id, _) = Require(user);
            await profiles.CreateDeletionRequestAsync(id, body.Password, ct);
            return Results.Accepted();
        });

        me.MapDelete("/me/deletion-requests/current", async (
            [FromServices] CustomerProfileService profiles,
            [FromServices] ICurrentUser user,
            CancellationToken ct) =>
        {
            var (id, _) = Require(user);
            await profiles.CancelDeletionRequestAsync(id, ct);
            return Results.NoContent();
        });
    }

    private static (Guid UserId, Guid TenantId) Require(ICurrentUser user)
    {
        if (user.UserId is not Guid id || user.TenantId is not Guid tid)
        {
            throw new ForbiddenException();
        }

        return (id, tid);
    }
}
