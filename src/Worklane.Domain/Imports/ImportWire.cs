using Worklane.Domain.Enums;

namespace Worklane.Domain.Imports;

/// <summary>
/// Stable spellings for import enums. The database and the JSON contract both use these,
/// not the CLR names (<c>SkippedExisting</c> is <c>skipped_existing</c>).
/// </summary>
public static class ImportWire
{
    public static string Source(ImportSource value) => value switch
    {
        ImportSource.Generic => "generic",
        ImportSource.Jira => "jira",
        ImportSource.Asana => "asana",
        ImportSource.Trello => "trello",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };

    public static string BatchStatus(ImportBatchStatus value) => value switch
    {
        ImportBatchStatus.Uploaded => "uploaded",
        ImportBatchStatus.Mapped => "mapped",
        ImportBatchStatus.Previewed => "previewed",
        ImportBatchStatus.Committed => "committed",
        ImportBatchStatus.Undone => "undone",
        ImportBatchStatus.Expired => "expired",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };

    public static string RowStatus(ImportRowStatus value) => value switch
    {
        ImportRowStatus.Pending => "pending",
        ImportRowStatus.Ready => "ready",
        ImportRowStatus.Warning => "warning",
        ImportRowStatus.Invalid => "invalid",
        ImportRowStatus.Created => "created",
        ImportRowStatus.SkippedExisting => "skipped_existing",
        ImportRowStatus.SkippedArchived => "skipped_archived",
        ImportRowStatus.SkippedInvalid => "skipped_invalid",
        ImportRowStatus.SkippedSourceArchived => "skipped_source_archived",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };

    public static string Format(ExportFormat value) => value switch
    {
        ExportFormat.Csv => "csv",
        ExportFormat.Json => "json",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };

    public static string UndoReason(ImportUndoReason value) => value switch
    {
        ImportUndoReason.EditedAfterImport => "edited_after_import",
        ImportUndoReason.HasActiveChildren => "has_active_children",
        ImportUndoReason.AlreadyArchived => "already_archived",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };

    public static bool TryParseSource(string? text, out ImportSource source)
    {
        switch (text)
        {
            case "generic": source = ImportSource.Generic; return true;
            case "jira": source = ImportSource.Jira; return true;
            case "asana": source = ImportSource.Asana; return true;
            case "trello": source = ImportSource.Trello; return true;
            default: source = default; return false;
        }
    }

    public static ImportSource ParseSource(string text) =>
        TryParseSource(text, out var source)
            ? source
            : throw new ArgumentOutOfRangeException(nameof(text), text, null);

    public static ImportBatchStatus ParseBatchStatus(string text) => text switch
    {
        "uploaded" => ImportBatchStatus.Uploaded,
        "mapped" => ImportBatchStatus.Mapped,
        "previewed" => ImportBatchStatus.Previewed,
        "committed" => ImportBatchStatus.Committed,
        "undone" => ImportBatchStatus.Undone,
        "expired" => ImportBatchStatus.Expired,
        _ => throw new ArgumentOutOfRangeException(nameof(text), text, null)
    };

    public static ImportRowStatus ParseRowStatus(string text) => text switch
    {
        "pending" => ImportRowStatus.Pending,
        "ready" => ImportRowStatus.Ready,
        "warning" => ImportRowStatus.Warning,
        "invalid" => ImportRowStatus.Invalid,
        "created" => ImportRowStatus.Created,
        "skipped_existing" => ImportRowStatus.SkippedExisting,
        "skipped_archived" => ImportRowStatus.SkippedArchived,
        "skipped_invalid" => ImportRowStatus.SkippedInvalid,
        "skipped_source_archived" => ImportRowStatus.SkippedSourceArchived,
        _ => throw new ArgumentOutOfRangeException(nameof(text), text, null)
    };

    public static ExportFormat ParseFormat(string text) => text switch
    {
        "csv" => ExportFormat.Csv,
        "json" => ExportFormat.Json,
        _ => throw new ArgumentOutOfRangeException(nameof(text), text, null)
    };

    public static bool TryParseFormat(string? text, out ExportFormat format)
    {
        switch (text)
        {
            case "csv": format = ExportFormat.Csv; return true;
            case "json": format = ExportFormat.Json; return true;
            default: format = default; return false;
        }
    }
}
