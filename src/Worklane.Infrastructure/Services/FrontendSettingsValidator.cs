using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Worklane.Application.Common.Options;

namespace Worklane.Infrastructure.Services;

public sealed class FrontendSettingsValidator(IHostEnvironment environment) : IValidateOptions<FrontendSettings>
{
    public ValidateOptionsResult Validate(string? name, FrontendSettings options)
    {
        var problem = Problem(options.BaseUrl, requirePublicHost: !environment.IsDevelopment());
        return problem is null ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problem);
    }

    internal static string? Problem(string? baseUrl, bool requirePublicHost)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return requirePublicHost
                ? "Frontend:BaseUrl is required outside Development."
                : null;
        }

        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Host))
        {
            return "Frontend:BaseUrl must be an absolute http or https URL.";
        }

        if (requirePublicHost && uri.IsLoopback)
            return "Frontend:BaseUrl must not use a loopback host outside Development.";

        return null;
    }
}
