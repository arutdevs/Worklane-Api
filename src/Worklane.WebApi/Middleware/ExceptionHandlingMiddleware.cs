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

        var detail = statusCode switch
        {
            HttpStatusCode.InternalServerError => "An unexpected error occurred.",
            HttpStatusCode.RequestEntityTooLarge when exception is not PayloadTooLargeException =>
                "The request body exceeds the size limit.",
            _ => exception.Message
        };

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

        context.Response.ContentType = "application/problem+json";
        context.Response.StatusCode = (int)statusCode;
        await context.Response.WriteAsJsonAsync(problemDetails);
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
                if (error.Exception is BadHttpRequestException bad &&
                    bad.StatusCode == StatusCodes.Status413PayloadTooLarge)
                    return true;

                if (error.ErrorMessage.Contains("Request body too large", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }
}
