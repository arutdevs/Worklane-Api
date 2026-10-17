using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.Extensions.Options;
using Worklane.Application.Common.Options;

namespace Worklane.WebApi.Filters;

/// <summary>
/// Marks the attachment upload action. The convention turns this into a
/// <see cref="RequestSizeLimitAttribute"/> from <see cref="FileStorageSettings.MaxBytes"/>
/// plus <see cref="FileStorageSettings.UploadEnvelopeBytes"/>, so the transport ceiling
/// follows configuration instead of a fixed 11 MB.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class AttachmentUploadLimitAttribute : Attribute
{
}

/// <summary>
/// Applies the configured upload body ceiling when the application model is built.
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
