using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using Worklane.Application.Common.Options;

namespace Worklane.WebApi.Filters;

/// <summary>
/// Marks the import upload and applies <see cref="ImportSettings.RequestLimit"/>
/// (<c>Imports:MaxBytes</c> plus the one-mebibyte multipart envelope).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ImportUploadLimitAttribute : Attribute, IFilterFactory, IOrderedFilter
{
    public bool IsReusable => false;

    /// <summary>After <c>RequestSizeLimit</c> (order 900), so this request's options win.</summary>
    public int Order => 1000;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider)
    {
        var settings = serviceProvider.GetRequiredService<IOptions<ImportSettings>>().Value;
        return new ImportUploadLimitFilter(settings.RequestLimit);
    }
}

public sealed class ImportUploadLimitFilter : IAsyncResourceFilter
{
    private readonly long _limit;

    public ImportUploadLimitFilter(long limit)
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

public sealed class ImportUploadLimitConvention : IApplicationModelConvention
{
    private readonly long _limit;

    public ImportUploadLimitConvention(long limit)
    {
        _limit = limit;
    }

    public void Apply(ApplicationModel application)
    {
        foreach (var controller in application.Controllers)
        {
            foreach (var action in controller.Actions)
            {
                if (action.ActionMethod.IsDefined(typeof(ImportUploadLimitAttribute), inherit: false))
                    action.Filters.Add(new RequestSizeLimitAttribute(_limit));
            }
        }
    }
}

public sealed class ImportUploadLimitSetup : IConfigureOptions<MvcOptions>
{
    private readonly IOptions<ImportSettings> _imports;

    public ImportUploadLimitSetup(IOptions<ImportSettings> imports)
    {
        _imports = imports;
    }

    public void Configure(MvcOptions options)
    {
        options.Conventions.Add(new ImportUploadLimitConvention(_imports.Value.RequestLimit));
    }
}
