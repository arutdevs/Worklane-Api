using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Worklane.Application.Common.Options;

namespace Worklane.Infrastructure.Services;

public sealed class S3StorageOptionsValidator(IHostEnvironment environment) : IValidateOptions<S3StorageOptions>
{
    public ValidateOptionsResult Validate(string? name, S3StorageOptions options)
    {
        var relaxed = environment.IsDevelopment() || environment.IsEnvironment("Testing");
        var problems = Problems(options, relaxed);
        return problems.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(problems);
    }

    internal static IReadOnlyList<string> Problems(S3StorageOptions options, bool relaxedEnvironment)
    {
        var failures = new List<string>();
        Uri? serviceUri = null;
        var serviceUrl = options.ServiceUrl?.Trim();
        if (!Uri.TryCreate(serviceUrl, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(parsed.Host))
        {
            failures.Add("FileStorage:S3:ServiceUrl must be an absolute http or https URL.");
        }
        else
        {
            serviceUri = parsed;
        }

        if (options.RequireHttps && serviceUri is not null && serviceUri.Scheme != Uri.UriSchemeHttps)
            failures.Add("FileStorage:S3:ServiceUrl must use https when FileStorage:S3:RequireHttps is true.");

        if (string.IsNullOrWhiteSpace(options.Region))
            failures.Add("FileStorage:S3:Region is required.");

        if (!IsBucketName(options.Bucket))
            failures.Add("FileStorage:S3:Bucket must be a valid bucket name.");

        if (string.IsNullOrWhiteSpace(options.AccessKey))
            failures.Add("FileStorage:S3:AccessKey is required.");

        if (string.IsNullOrWhiteSpace(options.SecretKey))
            failures.Add("FileStorage:S3:SecretKey is required.");

        if (options.PresignExpirySeconds is < 1 or > 604800)
            failures.Add("FileStorage:S3:PresignExpirySeconds must be between 1 and 604800.");

        if (!string.IsNullOrWhiteSpace(options.PublicServiceUrl)
            && !IsHttpUrl(options.PublicServiceUrl))
        {
            failures.Add("FileStorage:S3:PublicServiceUrl must be an absolute http or https URL when set.");
        }

        if (!relaxedEnvironment)
        {
            if (serviceUri is not null && (serviceUri.Scheme != Uri.UriSchemeHttps || IsBlockedHost(serviceUri)))
                failures.Add("FileStorage:S3:ServiceUrl is not allowed outside Development and Testing.");

            if (!options.RequireHttps)
                failures.Add("FileStorage:S3:RequireHttps must be true outside Development and Testing.");

            if (string.Equals(options.Bucket?.Trim(), "worklane-dev-files", StringComparison.OrdinalIgnoreCase))
                failures.Add("FileStorage:S3:Bucket worklane-dev-files is not allowed outside Development and Testing.");

            if (!string.IsNullOrWhiteSpace(options.PublicServiceUrl))
                failures.Add("FileStorage:S3:PublicServiceUrl is not allowed outside Development and Testing.");
        }

        return failures;
    }

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && !string.IsNullOrEmpty(uri.Host);

    internal static bool IsBlockedHost(Uri uri)
    {
        if (uri.IsLoopback)
            return true;

        var host = uri.IdnHost.Trim().Trim('[', ']');
        if (host.Equals("s3", StringComparison.OrdinalIgnoreCase)
            || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || host.Equals("::1", StringComparison.OrdinalIgnoreCase)
            || host.Equals("host.docker.internal", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !host.Contains('.', StringComparison.Ordinal);
    }

    private static bool IsBucketName(string? bucket)
    {
        if (string.IsNullOrWhiteSpace(bucket))
            return false;

        bucket = bucket.Trim();
        if (bucket.Length is < 3 or > 63 || bucket.Contains("..", StringComparison.Ordinal))
            return false;

        if (bucket[0] is '.' or '-' || bucket[^1] is '.' or '-')
            return false;

        foreach (var ch in bucket)
        {
            var ok = ch is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-';
            if (!ok)
                return false;
        }

        if (IPAddress.TryParse(bucket, out _))
            return false;

        return true;
    }
}
