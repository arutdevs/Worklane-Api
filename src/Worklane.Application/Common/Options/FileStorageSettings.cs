using Microsoft.Extensions.Configuration;

namespace Worklane.Application.Common.Options;

public class FileStorageSettings
{
    public const string SectionName = "FileStorage";

    /// <summary>Extra room for the multipart envelope above the default per-file cap.</summary>
    public const long UploadEnvelopeBytes = 1024 * 1024;

    /// <summary>Kestrel's ceiling for every endpoint that does not raise it with RequestSizeLimit.</summary>
    public const long GlobalRequestBodyLimitBytes = 2 * 1024 * 1024;

    public const long DefaultMaxBytes = 10 * 1024 * 1024;

    /// <summary>
    /// Transport ceiling for attachment upload. <c>[RequestSizeLimit]</c> needs a constant,
    /// so this is the default file cap plus the multipart envelope, not the configured
    /// <see cref="MaxBytes"/>.
    /// </summary>
    public const long AttachmentRequestLimitBytes = DefaultMaxBytes + UploadEnvelopeBytes;

    /// <summary>Directory for the local-disk provider. Relative paths are under the content root.</summary>
    public string LocalPath { get; set; } = "data/attachments";

    /// <summary>Per-file cap. Default 10 MB. A larger file is rejected with 413.</summary>
    public long MaxBytes { get; set; } = DefaultMaxBytes;

    /// <summary>
    /// Workspace storage cap, the sum of attachment <c>SizeBytes</c>. Exceeding it is 409.
    /// </summary>
    public long WorkspaceQuotaBytes { get; set; } = 1024L * 1024 * 1024;

    /// <summary>
    /// Types we are willing to record. A configured list replaces this default;
    /// it is not appended to it. The upload path derives the type from the file
    /// extension and ignores the client Content-Type header.
    /// </summary>
    public string[] AllowedContentTypes { get; set; } = DefaultContentTypes;

    /// <summary>Extensions we accept. A configured list replaces this default.</summary>
    public string[] AllowedExtensions { get; set; } = DefaultExtensions;

    public long AttachmentRequestLimit => MaxBytes + UploadEnvelopeBytes;

    public static readonly string[] DefaultContentTypes =
    [
        "image/png",
        "image/jpeg",
        "image/gif",
        "image/webp",
        "application/pdf",
        "text/plain",
        "text/csv",
        "application/zip",
        "application/json",
        "application/msword",
        "application/vnd.ms-excel",
        "application/vnd.ms-powerpoint",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/vnd.openxmlformats-officedocument.presentationml.presentation",
    ];

    public static readonly string[] DefaultExtensions =
    [
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".pdf",
        ".txt", ".csv", ".json", ".zip",
        ".doc", ".xls", ".ppt", ".docx", ".xlsx", ".pptx",
    ];

    /// <summary>
    /// Binds the FileStorage section so an explicit allow-list replaces the built-in
    /// one. The configuration binder appends to collections that already have values;
    /// reading the arrays with <c>Get&lt;string[]&gt;()</c> takes only what was configured.
    /// </summary>
    public static FileStorageSettings FromConfiguration(IConfiguration configuration)
    {
        var settings = new FileStorageSettings();
        var section = configuration.GetSection(SectionName);
        if (!section.Exists()) return settings;

        var path = section[nameof(LocalPath)];
        if (!string.IsNullOrWhiteSpace(path))
            settings.LocalPath = path;

        // Non-positive values are kept so startup validation can reject them.
        // A missing or unparseable value stays on the positive default.
        if (long.TryParse(section[nameof(MaxBytes)], out var maxBytes))
            settings.MaxBytes = maxBytes;

        if (long.TryParse(section[nameof(WorkspaceQuotaBytes)], out var quota))
            settings.WorkspaceQuotaBytes = quota;

        if (section.GetSection(nameof(AllowedContentTypes)).Exists())
            settings.AllowedContentTypes = section.GetSection(nameof(AllowedContentTypes)).Get<string[]>() ?? [];

        if (section.GetSection(nameof(AllowedExtensions)).Exists())
            settings.AllowedExtensions = section.GetSection(nameof(AllowedExtensions)).Get<string[]>() ?? [];

        return settings;
    }
}
