using AICOS.BuildingBlocks.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AICOS.BuildingBlocks.Http;

/// <summary>Maps exceptions to RFC 7807 <c>application/problem+json</c> responses. Unknown exceptions become a generic 500.</summary>
internal sealed class GlobalExceptionHandler(
    IProblemDetailsService problemDetails,
    IHostEnvironment environment,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            httpContext.Response.StatusCode = 499; // client closed request
            return true;
        }

        var problem = exception switch
        {
            ValidationFailedException validation => Build(validation, StatusCodes.Status400BadRequest, extensions: new() { ["errors"] = validation.Errors }),
            AppException app => Build(app, (int)app.StatusCode),
            DbUpdateConcurrencyException => new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Concurrency conflict",
                Detail = "The record was modified by someone else. Reload and try again.",
                Extensions = { ["errorCode"] = "concurrency_conflict" },
            },
            _ => new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "An unexpected error occurred",
                Detail = environment.IsDevelopment() ? exception.ToString() : null,
            },
        };

        if (problem.Status >= 500)
        {
            logger.LogError(exception, "Unhandled exception");
        }
        else
        {
            logger.LogWarning("Request failed with {Status}: {Message}", problem.Status, exception.Message);
        }

        if (httpContext.Items[CorrelationIdMiddleware.HeaderName] is string correlationId)
        {
            problem.Extensions["correlationId"] = correlationId;
        }

        httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        });
    }

    private static ProblemDetails Build(AppException exception, int status, Dictionary<string, object?>? extensions = null)
    {
        var problem = new ProblemDetails
        {
            Status = status,
            Title = exception.Title,
            Detail = exception.Message,
        };

        if (exception.ErrorCode is not null)
        {
            problem.Extensions["errorCode"] = exception.ErrorCode;
        }

        if (extensions is not null)
        {
            foreach (var (key, value) in extensions)
            {
                problem.Extensions[key] = value;
            }
        }

        return problem;
    }
}
