namespace Worklane.Domain.Enums;

/// <summary>Where an imported card came from. Stored as the wire name, not the CLR name.</summary>
public enum ImportSource
{
    Generic,
    Jira,
    Asana,
    Trello
}

/// <summary>Lifecycle of one uploaded file. Stored as the wire name.</summary>
public enum ImportBatchStatus
{
    Uploaded,
    Mapped,
    Previewed,
    Committed,
    Undone,
    Expired
}

/// <summary>Outcome of one data row. Stored as the wire name.</summary>
public enum ImportRowStatus
{
    Pending,
    Ready,
    Warning,
    Invalid,
    Created,
    SkippedExisting,
    SkippedArchived,
    SkippedInvalid,
    SkippedSourceArchived
}

/// <summary>Export file format. Stored as the wire name.</summary>
public enum ExportFormat
{
    Csv,
    Json
}

/// <summary>Why undo left a card active. Wire names are the API values.</summary>
public enum ImportUndoReason
{
    EditedAfterImport,
    HasActiveChildren,
    AlreadyArchived
}
