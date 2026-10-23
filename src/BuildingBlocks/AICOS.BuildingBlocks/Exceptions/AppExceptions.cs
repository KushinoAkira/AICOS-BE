using System.Net;

namespace AICOS.BuildingBlocks.Exceptions;

/// <summary>
/// Expected, business-level failures. Thrown from handlers/services and translated to
/// RFC 7807 ProblemDetails by <c>GlobalExceptionHandler</c>. Anything else becomes a 500.
/// </summary>
public abstract class AppException(string message, HttpStatusCode statusCode, string title, string? errorCode = null)
    : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string Title { get; } = title;

    /// <summary>Stable machine-readable code, e.g. "BR-06" or "order.not_found". Clients may switch on it.</summary>
    public string? ErrorCode { get; } = errorCode;
}

public sealed class NotFoundException(string resource, object key)
    : AppException($"{resource} '{key}' was not found.", HttpStatusCode.NotFound, "Resource not found", "not_found");

public sealed class ConflictException(string message, string? errorCode = "conflict")
    : AppException(message, HttpStatusCode.Conflict, "Conflict", errorCode);

public sealed class ForbiddenException(string message = "You do not have permission to perform this action.")
    : AppException(message, HttpStatusCode.Forbidden, "Forbidden", "forbidden");

public sealed class UnauthorizedException(string message = "Authentication failed.", string errorCode = "unauthorized")
    : AppException(message, HttpStatusCode.Unauthorized, "Unauthorized", errorCode);

/// <summary>A business rule (BR-xx in the BA documents) was violated although the request itself was well-formed.</summary>
public sealed class BusinessRuleException(string errorCode, string message)
    : AppException(message, HttpStatusCode.UnprocessableEntity, "Business rule violated", errorCode);

public sealed class ValidationFailedException : AppException
{
    public ValidationFailedException(IDictionary<string, string[]> errors)
        : base("One or more validation errors occurred.", HttpStatusCode.BadRequest, "Validation failed", "validation_failed")
    {
        Errors = errors;
    }

    public ValidationFailedException(string field, string error)
        : this(new Dictionary<string, string[]> { [field] = [error] })
    {
    }

    public IDictionary<string, string[]> Errors { get; }
}
