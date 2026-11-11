namespace Worklane.Application.Common.Options;

/// <summary>
/// <c>FileStorage:S3</c>. Names match <c>FileStorage__S3__*</c> in compose.dev.yml.
/// Validated at startup only when <c>FileStorage:Provider</c> is <c>S3</c>.
/// </summary>
public sealed class S3StorageOptions
{
    public const string SectionName = FileStorageSettings.SectionName + ":S3";

    public string ServiceUrl { get; set; } = "";

    /// <summary>Signing region. Cloudflare R2 uses <c>auto</c>.</summary>
    public string Region { get; set; } = "";

    public string Bucket { get; set; } = "";

    public string AccessKey { get; set; } = "";

    public string SecretKey { get; set; } = "";

    /// <summary>Path-style addressing. RustFS and R2 need this; Spaces sets it false.</summary>
    public bool ForcePathStyle { get; set; } = true;

    /// <summary>When true, <see cref="ServiceUrl"/> must be https.</summary>
    public bool RequireHttps { get; set; } = true;

    public int PresignExpirySeconds { get; set; } = 60;

    public string? PublicServiceUrl { get; set; }
}
