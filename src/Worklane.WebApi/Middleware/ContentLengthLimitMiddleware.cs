using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Worklane.Application.Common.Options;
using Worklane.WebApi.Filters;

namespace Worklane.WebApi.Middleware;

/// <summary>
/// Answers 413 from Content-Length before the body is read. MVC otherwise turns
/// Kestrel's body-limit failure into a 400 model error and keeps only the message,
/// which includes the configured byte limit. Attachment upload uses
/// <see cref="FileStorageSettings.AttachmentRequestLimit"/> and raises Kestrel to
/// that ceiling here, because this middleware runs before the upload filter.
/// </summary>
public class ContentLengthLimitMiddleware
{
    private readonly RequestDelegate _next;
    private readonly FileStorageSettings _storage;

    public ContentLengthLimitMiddleware(RequestDelegate next, IOptions<FileStorageSettings> storage)
    {
        _next = next;
        _storage = storage.Value;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var limit = ResolveLimit(context, _storage);
        if (DeclaredBodyExceedsLimit(context.Request.ContentLength, limit))
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await context.Response.WriteAsJsonAsync(
                new ProblemDetails
                {
                    Status = StatusCodes.Status413PayloadTooLarge,
                    Title = "Payload Too Large",
                    Detail = "The request body exceeds the size limit.",
                    Instance = context.Request.Path
                },
                options: null,
                contentType: "application/problem+json");
            return;
        }

        // Raise Kestrel only for a body this route accepts. Doing it on the
        // rejection path makes Kestrel read the body while this 413 is written.
        if (IsAttachmentUpload(context))
            RaiseRequestCeiling(context, _storage.AttachmentRequestLimit);

        await _next(context);
    }

    internal static long? ResolveLimit(HttpContext context, FileStorageSettings storage)
    {
        if (IsAttachmentUpload(context))
            return storage.AttachmentRequestLimit;

        var metadata = context.GetEndpoint()?.Metadata.GetMetadata<IRequestSizeLimitMetadata>();
        if (metadata is not null)
            return metadata.MaxRequestBodySize;

        return context.Features.Get<IHttpMaxRequestBodySizeFeature>()?.MaxRequestBodySize;
    }

    private static bool IsAttachmentUpload(HttpContext context)
        => context.GetEndpoint()?.Metadata.GetMetadata<AttachmentUploadLimitAttribute>() is not null;

    private static void RaiseRequestCeiling(HttpContext context, long limit)
    {
        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
            feature.MaxRequestBodySize = limit;
    }

    internal static bool DeclaredBodyExceedsLimit(long? contentLength, long? limit)
        => contentLength is long length && limit is long max && length > max;
}
