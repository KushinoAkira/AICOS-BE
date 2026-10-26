namespace AICOS.Payments.Api.Features.Payments;

public sealed class PaymentMaintenanceMiddleware(RequestDelegate next, IConfiguration configuration)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        if (!configuration.GetValue("Payments:MutationsEnabled", true) &&
            request.Path.StartsWithSegments("/api/v1/payments") &&
            !HttpMethods.IsGet(request.Method) &&
            !HttpMethods.IsHead(request.Method) &&
            !HttpMethods.IsOptions(request.Method))
        {
            context.Response.Headers.RetryAfter = "60";
            await Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Payment maintenance",
                detail: "Payment mutations are temporarily paused.").ExecuteAsync(context);
            return;
        }

        await next(context);
    }
}
