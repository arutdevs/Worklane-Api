using System.Net;
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
        var (statusCode, title) = exception switch
        {
            ValidationException => (HttpStatusCode.BadRequest, "Validation Failed"),
            BadRequestException => (HttpStatusCode.BadRequest, "Bad Request"),
            NotFoundException => (HttpStatusCode.NotFound, "Not Found"),
            ForbiddenAccessException => (HttpStatusCode.Forbidden, "Forbidden"),
            AuthenticationFailedException => (HttpStatusCode.Unauthorized, "Unauthorized"),
            ConflictException => (HttpStatusCode.Conflict, "Conflict"),
            PayloadTooLargeException => (HttpStatusCode.RequestEntityTooLarge, "Payload Too Large"),
            UnsupportedMediaTypeException => (HttpStatusCode.UnsupportedMediaType, "Unsupported Media Type"),
            BadHttpRequestException bad => ((HttpStatusCode)bad.StatusCode,
                bad.StatusCode == StatusCodes.Status413PayloadTooLarge ? "Payload Too Large" : "Bad Request"),
            InvalidDataException { InnerException: BadHttpRequestException bad } => ((HttpStatusCode)bad.StatusCode,
                bad.StatusCode == StatusCodes.Status413PayloadTooLarge ? "Payload Too Large" : "Bad Request"),
            _ => (HttpStatusCode.InternalServerError, "Server Error")
        };

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

        if (exception is ValidationException validationException)
        {
            problemDetails.Extensions["errors"] = validationException.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());
        }

        context.Response.StatusCode = (int)statusCode;
        await context.Response.WriteAsJsonAsync(
            problemDetails,
            options: null,
            contentType: "application/problem+json");
    }

    /// <summary>
    /// Exceptions thrown for the client keep the message they were given.
    /// Anything else, including framework body-limit failures, gets a fixed
    /// sentence for that status so internal text never leaves the process.
    /// </summary>
    private static bool IsClientMessage(Exception exception) =>
        exception is ValidationException
            or BadRequestException
            or NotFoundException
            or ForbiddenAccessException
            or AuthenticationFailedException
            or ConflictException
            or PayloadTooLargeException
            or UnsupportedMediaTypeException;

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
            _ => "An unexpected error occurred."
        };
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
            return builtIn(context);

        return new ObjectResult(new ProblemDetails
        {
            Status = StatusCodes.Status413PayloadTooLarge,
            Title = "Payload Too Large",
            Detail = "The request body exceeds the size limit.",
            Instance = context.HttpContext.Request.Path
        })
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
