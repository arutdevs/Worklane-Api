using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using Worklane.Application.Common.Options;

namespace Worklane.WebApi.Filters;

/// <summary>
/// Marks the attachment upload action and applies
/// <see cref="FileStorageSettings.AttachmentRequestLimit"/> (configured
/// <see cref="FileStorageSettings.MaxBytes"/> plus one mebibyte). The limit is
/// read from options for the request. It is not the 11 MiB constant.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class AttachmentUploadLimitAttribute : Attribute, IFilterFactory, IOrderedFilter
{
    public bool IsReusable => false;

    /// <summary>After <c>RequestSizeLimit</c> (order 900), so this request's options win.</summary>
    public int Order => 1000;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider)
    {
        var settings = serviceProvider.GetRequiredService<IOptions<FileStorageSettings>>().Value;
        return new AttachmentUploadLimitFilter(settings.AttachmentRequestLimit);
    }
}

/// <summary>
/// Raises Kestrel's per-request body ceiling to the configured attachment limit.
/// </summary>
public sealed class AttachmentUploadLimitFilter : IAsyncResourceFilter
{
    private readonly long _limit;

    public AttachmentUploadLimitFilter(long limit)
    {
        _limit = limit;
    }

    public Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var feature = context.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
            feature.MaxRequestBodySize = _limit;

        return next();
    }
}

/// <summary>
/// Publishes the same configured ceiling as endpoint metadata so Content-Length
/// checks and tests can see <see cref="FileStorageSettings.AttachmentRequestLimit"/>.
/// </summary>
public sealed class AttachmentUploadLimitConvention : IApplicationModelConvention
{
    private readonly long _limit;

    public AttachmentUploadLimitConvention(long limit)
    {
        _limit = limit;
    }

    public void Apply(ApplicationModel application)
    {
        foreach (var controller in application.Controllers)
        {
            foreach (var action in controller.Actions)
            {
                if (action.ActionMethod.IsDefined(typeof(AttachmentUploadLimitAttribute), inherit: false))
                    action.Filters.Add(new RequestSizeLimitAttribute(_limit));
            }
        }
    }
}

/// <summary>Adds the upload size convention once file-storage options have been bound.</summary>
public sealed class AttachmentUploadLimitSetup : IConfigureOptions<MvcOptions>
{
    private readonly IOptions<FileStorageSettings> _storage;

    public AttachmentUploadLimitSetup(IOptions<FileStorageSettings> storage)
    {
        _storage = storage;
    }

    public void Configure(MvcOptions options)
    {
        options.Conventions.Add(new AttachmentUploadLimitConvention(_storage.Value.AttachmentRequestLimit));
    }
}
