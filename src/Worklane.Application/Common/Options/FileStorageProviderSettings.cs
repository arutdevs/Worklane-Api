namespace Worklane.Application.Common.Options;

/// <summary>
/// <c>FileStorage:Provider</c>. Missing or blank stays on the local disk.
/// </summary>
public sealed class FileStorageProviderSettings
{
    public const string SectionName = FileStorageSettings.SectionName;

    public string Provider { get; set; } = FileStorageProviders.Local;
}

public static class FileStorageProviders
{
    public const string Local = "Local";
    public const string S3 = "S3";

    public static bool IsS3(string? provider) =>
        string.Equals(provider?.Trim(), S3, StringComparison.OrdinalIgnoreCase);

    public static bool IsKnown(string? provider) =>
        string.IsNullOrWhiteSpace(provider)
        || string.Equals(provider.Trim(), Local, StringComparison.OrdinalIgnoreCase)
        || IsS3(provider);
}
