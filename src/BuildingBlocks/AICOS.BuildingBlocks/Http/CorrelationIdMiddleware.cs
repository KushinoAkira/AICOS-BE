using Serilog.Context;

namespace AICOS.BuildingBlocks.Http;

/// <summary>
/// Ensures every request carries an <c>X-Correlation-Id</c> (generated at the gateway if the client sent none),
/// echoes it on the response and attaches it to every log line. It is propagated to downstream calls by <c>ServiceCallHandler</c>.
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-Id";
    private const int MaxLength = 64;

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers[HeaderName].FirstOrDefault();
        if (!IsValid(correlationId))
        {
            correlationId = Guid.NewGuid().ToString("N");
            context.Request.Headers[HeaderName] = correlationId; // so the gateway forwards it to the service
        }

        context.Items[HeaderName] = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await next(context);
        }
    }

    private static bool IsValid(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= MaxLength
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
