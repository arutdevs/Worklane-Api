namespace Worklane.Application.Common.Options;

/// <summary>Caps for project import. File bytes are not counted against the attachment quota.</summary>
public class ImportSettings
{
    public const string SectionName = "Imports";

    public const long DefaultMaxBytes = 5_242_880;
    public const int DefaultMaxRows = 2000;
    public const int DefaultMaxColumns = 64;
    public const long DefaultMaxUncompressedBytes = 20_971_520;

    public long MaxBytes { get; set; } = DefaultMaxBytes;
    public int MaxRows { get; set; } = DefaultMaxRows;
    public int MaxColumns { get; set; } = DefaultMaxColumns;
    public long MaxUncompressedBytes { get; set; } = DefaultMaxUncompressedBytes;
    public int RetentionHours { get; set; } = 24;
    public int MaxOpenBatchesPerProject { get; set; } = 10;
    public int CommitCommandTimeoutSeconds { get; set; } = 60;
    public int RealtimeFanoutLimit { get; set; } = 50;

    public long RequestLimit => MaxBytes + FileStorageSettings.UploadEnvelopeBytes;
}
