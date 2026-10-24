using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.Ordering.Api.Services.Takeaway;
using Microsoft.AspNetCore.Mvc;

namespace AICOS.Ordering.Api.Features;

public static class PickupEndpoints
{
    public static void MapPickupEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/branches/{id:guid}/pickup-slots", async (
            Guid id,
            [FromQuery] DateOnly? date,
            [FromServices] PickupOrderService pickup,
            CancellationToken ct) =>
        {
            var day = date ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var slots = await pickup.GetPickupSlotsAsync(id, day, ct);
            return Results.Ok(slots);
        }).WithTags("Pickup").AllowAnonymous();

        var customer = api.MapGroup("/")
            .WithTags("Pickup")
            .RequireAuthorization(AicosPolicies.Customer);

        customer.MapPost("/pickup-orders", async (
            HttpContext http,
            [FromBody] CreatePickupOrderRequest request,
            [FromServices] PickupOrderService pickup,
            [FromServices] ICurrentUser user,
            CancellationToken ct) =>
        {
            var (customerId, tenantId) = RequireCustomer(user);
            http.Request.Headers.TryGetValue("Idempotency-Key", out var key);
            var (result, created) = await pickup.CreatePickupOrderAsync(customerId, tenantId, key.ToString(), request, ct);
            return created
                ? Results.Created($"/api/v1/orders/pickup-orders/{result.OrderId}", result)
                : Results.Ok(result);
        });

        customer.MapPost("/pickup-orders/{id:guid}/payment", async (
            Guid id,
            [FromServices] PickupOrderService pickup,
            [FromServices] ICurrentUser user,
            CancellationToken ct) =>
        {
            var (customerId, _) = RequireCustomer(user);
            return Results.Ok(await pickup.StartPaymentAsync(id, customerId, ct));
        });

        customer.MapPost("/pickup-orders/{id:guid}/cancel", async (
            Guid id,
            [FromServices] PickupOrderService pickup,
            [FromServices] ICurrentUser user,
            CancellationToken ct) =>
        {
            var (customerId, _) = RequireCustomer(user);
            await pickup.CancelAsync(id, customerId, asStaff: false, ct);
            return Results.NoContent();
        });

        customer.MapGet("/me/orders", async (
            [FromQuery] string? status,
            [FromQuery] string? cursor,
            [FromQuery] int? limit,
            [FromServices] PickupOrderService pickup,
            [FromServices] ICurrentUser user,
            CancellationToken ct) =>
        {
            var (customerId, _) = RequireCustomer(user);
            var (items, next) = await pickup.ListMyOrdersAsync(customerId, status, cursor, limit ?? 20, ct);
            return Results.Ok(new { items, nextCursor = next });
        });

        customer.MapGet("/pickup-orders/{id:guid}", async (
            Guid id,
            [FromServices] PickupOrderService pickup,
            [FromServices] ICurrentUser user,
            CancellationToken ct) =>
        {
            var (customerId, _) = RequireCustomer(user);
            return Results.Ok(await pickup.GetMyOrderAsync(id, customerId, ct));
        });

        customer.MapGet("/me/orders/{id:guid}", async (
            Guid id,
            [FromServices] PickupOrderService pickup,
            [FromServices] ICurrentUser user,
            CancellationToken ct) =>
        {
            var (customerId, _) = RequireCustomer(user);
            return Results.Ok(await pickup.GetMyOrderAsync(id, customerId, ct));
        });

        customer.MapPost("/pickup-orders/{id:guid}/feedback", async (
            Guid id,
            [FromBody] FeedbackRequest body,
            [FromServices] PickupOrderService pickup,
            [FromServices] ICurrentUser user,
            CancellationToken ct) =>
        {
            var (customerId, _) = RequireCustomer(user);
            await pickup.SubmitFeedbackAsync(id, customerId, body.Rating, body.Comment, ct);
            return Results.Created($"/api/v1/orders/pickup-orders/{id}/feedback", new { id });
        });

        customer.MapPost("/{id:guid}/feedback", async (
            Guid id,
            [FromBody] FeedbackRequest body,
            [FromServices] PickupOrderService pickup,
            [FromServices] ICurrentUser user,
            CancellationToken ct) =>
        {
            var (customerId, _) = RequireCustomer(user);
            await pickup.SubmitFeedbackAsync(id, customerId, body.Rating, body.Comment, ct);
            return Results.Created($"/api/v1/orders/{id}/feedback", new { id });
        });

        api.MapPost("/payments/webhook", async (
            HttpRequest http,
            [FromBody] PaymentWebhookRequest body,
            [FromServices] PickupOrderService pickup,
            [FromServices] IConfiguration config,
            CancellationToken ct) =>
        {
            var expected = config["Payments:WebhookSecret"] ?? "dev-webhook-secret";
            if (!http.Headers.TryGetValue("X-Webhook-Signature", out var signature) ||
                !string.Equals(signature.ToString(), expected, StringComparison.Ordinal))
            {
                return Results.Unauthorized();
            }

            await pickup.HandlePaymentWebhookAsync(body, ct);
            return Results.Ok();
        }).AllowAnonymous().WithTags("Pickup");

        api.MapPatch("/staff/orders/{id:guid}/status", async (
            Guid id,
            [FromBody] StaffStatusRequest body,
            [FromServices] PickupOrderService pickup,
            [FromServices] ICurrentUser user,
            CancellationToken ct) =>
        {
            if (user.UserId is not Guid staffId)
            {
                throw new ForbiddenException();
            }

            await pickup.ChangeStaffStatusAsync(id, staffId, body.To, body.PickupCode, ct);
            return Results.NoContent();
        }).RequireAuthorization(AicosPolicies.Staff).WithTags("Pickup");

        api.MapPost("/staff/orders/{id:guid}/cancel", async (
            Guid id,
            [FromServices] PickupOrderService pickup,
            [FromServices] ICurrentUser user,
            CancellationToken ct) =>
        {
            if (user.UserId is not Guid staffId)
            {
                throw new ForbiddenException();
            }

            await pickup.CancelAsync(id, staffId, asStaff: true, ct);
            return Results.NoContent();
        }).RequireAuthorization(AicosPolicies.Staff).WithTags("Pickup");
    }

    private static (Guid CustomerId, Guid TenantId) RequireCustomer(ICurrentUser user)
    {
        if (user.UserId is not Guid customerId || user.TenantId is not Guid tenantId)
        {
            throw new ForbiddenException();
        }

        return (customerId, tenantId);
    }
}
