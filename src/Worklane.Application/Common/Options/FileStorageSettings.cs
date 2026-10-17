using Microsoft.Extensions.Configuration;

namespace Worklane.Application.Common.Options;

public class FileStorageSettings
{
    public const string SectionName = "FileStorage";

    /// <summary>Extra room for the multipart envelope above <see cref="MaxBytes"/>.</summary>
    public const long UploadEnvelopeBytes = 1024 * 1024;

    /// <summary>Kestrel's default, kept for every endpoint except attachment upload.</summary>
    public const long GlobalRequestBodyLimitBytes = 30_000_000;

    /// <summary>Directory for the local-disk provider. Relative paths are under the content root.</summary>
    public string LocalPath { get; set; } = "data/attachments";

    /// <summary>Per-file cap. Default 10 MB. A larger body is rejected with 413.</summary>
    public long MaxBytes { get; set; } = 10 * 1024 * 1024;

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

        if (long.TryParse(section[nameof(MaxBytes)], out var maxBytes) && maxBytes > 0)
            settings.MaxBytes = maxBytes;

        if (section.GetSection(nameof(AllowedContentTypes)).Exists())
            settings.AllowedContentTypes = section.GetSection(nameof(AllowedContentTypes)).Get<string[]>() ?? [];

        if (section.GetSection(nameof(AllowedExtensions)).Exists())
            settings.AllowedExtensions = section.GetSection(nameof(AllowedExtensions)).Get<string[]>() ?? [];

        return settings;
    }
}
