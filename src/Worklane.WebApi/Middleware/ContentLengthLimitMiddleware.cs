using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;

namespace Worklane.WebApi.Middleware;

/// <summary>
/// Answers 413 from Content-Length before the body is read. MVC otherwise turns
/// Kestrel's body-limit failure into a 400 model error and keeps only the message,
/// which includes the configured byte limit.
/// </summary>
public class ContentLengthLimitMiddleware
{
    private readonly RequestDelegate _next;

    public ContentLengthLimitMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!DeclaredBodyExceedsLimit(context))
        {
            await _next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = StatusCodes.Status413PayloadTooLarge,
            Title = "Payload Too Large",
            Detail = "The request body exceeds the size limit.",
            Instance = context.Request.Path
        });
    }

    internal static bool DeclaredBodyExceedsLimit(HttpContext context)
    {
        if (context.Request.ContentLength is not long length)
            return false;

        var metadata = context.GetEndpoint()?.Metadata.GetMetadata<IRequestSizeLimitMetadata>();
        long? limit = metadata is not null
            ? metadata.MaxRequestBodySize
            : context.Features.Get<IHttpMaxRequestBodySizeFeature>()?.MaxRequestBodySize;

        return limit is long max && length > max;
    }
}
