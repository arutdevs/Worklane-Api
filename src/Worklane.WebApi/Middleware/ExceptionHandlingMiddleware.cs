using System.Diagnostics;
using System.Net;
using System.Text;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Worklane.Application.Common.Exceptions;

namespace Worklane.WebApi.Middleware;

public class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            await HandleAsync(context, ex);
        }
    }

    private async Task HandleAsync(HttpContext context, Exception exception)
    {
        var statusCode = exception switch
        {
            AppException client => client.StatusCode,
            ValidationException => HttpStatusCode.BadRequest,
            BadHttpRequestException bad => (HttpStatusCode)bad.StatusCode,
            InvalidDataException { InnerException: BadHttpRequestException bad } => (HttpStatusCode)bad.StatusCode,
            _ => HttpStatusCode.InternalServerError
        };
        var title = exception is ValidationException ? "Validation Failed" : TitleFor(statusCode);

        if (statusCode == HttpStatusCode.InternalServerError)
        {
            _logger.LogError(exception, "Unhandled exception processing {Method} {Path}", context.Request.Method, context.Request.Path);
        }
        else if (!IsClientMessage(exception))
        {
            _logger.LogWarning(exception, "Rejected {Method} {Path} with status {Status}", context.Request.Method, context.Request.Path, (int)statusCode);
        }

        var detail = ClientDetail(exception, statusCode);

        var problemDetails = new ProblemDetails
        {
            Status = (int)statusCode,
            Title = title,
            Detail = detail,
            Instance = context.Request.Path
        };

        problemDetails.Extensions["code"] = CodeFor(exception, statusCode);
        if (statusCode == HttpStatusCode.InternalServerError)
            problemDetails.Extensions["traceId"] = Activity.Current?.Id ?? context.TraceIdentifier;

        if (exception is AppException clientError)
        {
            foreach (var (key, value) in clientError.Params)
            {
                if (!ReservedExtensions.Contains(key))
                    problemDetails.Extensions[key] = value;
            }
        }

        if (exception is ValidationException validationException)
        {
            problemDetails.Extensions["errors"] = validationException.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());
            problemDetails.Extensions["fieldCodes"] = validationException.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(g => g.Key, g => g.Select(e => ToFieldCode(e.ErrorCode)).ToArray());
        }

        context.Response.ContentType = "application/problem+json";
        context.Response.StatusCode = (int)statusCode;
        await context.Response.WriteAsJsonAsync(problemDetails);
    }

    /// <summary>
    /// Exceptions thrown for the client keep the message they were given.
    /// Anything else, including framework body-limit failures, gets a fixed
    /// sentence for that status so internal text never leaves the process.
    /// </summary>
    private static bool IsClientMessage(Exception exception) =>
        exception is AppException or ValidationException;

    private static string TitleFor(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.BadRequest => "Bad Request",
        HttpStatusCode.Unauthorized => "Unauthorized",
        HttpStatusCode.Forbidden => "Forbidden",
        HttpStatusCode.NotFound => "Not Found",
        HttpStatusCode.Conflict => "Conflict",
        HttpStatusCode.RequestEntityTooLarge => "Payload Too Large",
        HttpStatusCode.UnsupportedMediaType => "Unsupported Media Type",
        HttpStatusCode.TooManyRequests => "Too Many Requests",
        _ => "Server Error"
    };

    private static string ClientDetail(Exception exception, HttpStatusCode statusCode)
    {
        if (IsClientMessage(exception))
            return exception.Message;

        return statusCode switch
        {
            HttpStatusCode.BadRequest => "The request is invalid.",
            HttpStatusCode.Unauthorized => "Authentication is required.",
            HttpStatusCode.Forbidden => "You do not have access to this resource.",
            HttpStatusCode.NotFound => "The resource was not found.",
            HttpStatusCode.Conflict => "The request conflicts with the current state.",
            HttpStatusCode.RequestEntityTooLarge => "The request body exceeds the size limit.",
            HttpStatusCode.UnsupportedMediaType => "The file type is not allowed.",
            HttpStatusCode.TooManyRequests => "Too many requests.",
            _ => "An unexpected error occurred."
        };
    }

    private static readonly HashSet<string> ReservedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "type", "title", "status", "detail", "instance", "code", "errors", "fieldCodes", "traceId"
    };

    private static string CodeFor(Exception exception, HttpStatusCode statusCode) => exception switch
    {
        AppException app => app.Code,
        ValidationException => "validation_failed",
        BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge } => "request_too_large",
        BadHttpRequestException => "invalid_request_body",
        InvalidDataException { InnerException: BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge } } => "request_too_large",
        InvalidDataException { InnerException: BadHttpRequestException } => "invalid_request_body",
        _ => statusCode switch
        {
            HttpStatusCode.BadRequest => "bad_request",
            HttpStatusCode.Unauthorized => "unauthorized",
            HttpStatusCode.Forbidden => "forbidden",
            HttpStatusCode.NotFound => "not_found",
            HttpStatusCode.Conflict => "conflict",
            HttpStatusCode.RequestEntityTooLarge => "request_too_large",
            HttpStatusCode.UnsupportedMediaType => "unsupported_media_type",
            HttpStatusCode.TooManyRequests => "rate_limited",
            _ => "internal_error"
        }
    };

    /// <summary>
    /// FluentValidation's default error code is the validator class name
    /// (<c>NotEmptyValidator</c>). A code that is already snake_case is kept.
    /// </summary>
    private static string ToFieldCode(string? errorCode)
    {
        if (string.IsNullOrWhiteSpace(errorCode))
            return "invalid";

        if (errorCode.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_'))
            return errorCode;

        var name = errorCode.EndsWith("Validator", StringComparison.Ordinal)
            ? errorCode[..^"Validator".Length]
            : errorCode;

        var builder = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && i > 0 && builder.Length > 0 && builder[^1] != '_')
                builder.Append('_');
            if (c != '_')
                builder.Append(char.ToLowerInvariant(c));
        }

        return builder.Length == 0 ? "invalid" : builder.ToString();
    }

    /// <summary>
    /// Kestrel enforces the attachment body limit while the form is read. The form
    /// value provider catches that and stores it in model state, and [ApiController]
    /// would otherwise answer 400 before the action runs. Keep every other invalid
    /// model state on the built-in response.
    /// </summary>
    public static IActionResult InvalidModelStateResponse(ActionContext context, Func<ActionContext, IActionResult> builtIn)
    {
        if (!ModelStateRecordsBodyTooLarge(context.ModelState))
        {
            var result = builtIn(context);
            if (result is ObjectResult { Value: ProblemDetails problem } && !problem.Extensions.ContainsKey("code"))
                problem.Extensions["code"] = "invalid_request_body";
            return result;
        }

        var tooLarge = new ProblemDetails
        {
            Status = StatusCodes.Status413PayloadTooLarge,
            Title = "Payload Too Large",
            Detail = "The request body exceeds the size limit.",
            Instance = context.HttpContext.Request.Path
        };
        tooLarge.Extensions["code"] = "request_too_large";
        return new ObjectResult(tooLarge)
        {
            StatusCode = StatusCodes.Status413PayloadTooLarge,
            ContentTypes = { "application/problem+json" }
        };
    }

    private static bool ModelStateRecordsBodyTooLarge(ModelStateDictionary modelState)
    {
        foreach (var entry in modelState.Values)
        {
            foreach (var error in entry.Errors)
            {
                if (IsPayloadTooLarge(error.Exception))
                    return true;
            }
        }

        return false;
    }

    private static bool IsPayloadTooLarge(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is BadHttpRequestException bad && bad.StatusCode == StatusCodes.Status413PayloadTooLarge)
                return true;
        }

        return false;
    }
}
