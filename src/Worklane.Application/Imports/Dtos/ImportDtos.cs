using System.Text.Json.Serialization;
using Worklane.Domain.Enums;

namespace Worklane.Application.Imports.Dtos;

public sealed class ColumnAssignment
{
    public string Header { get; set; } = "";
    public string Target { get; set; } = "";
}

public sealed class ColumnMapping
{
    public List<ColumnAssignment> Assignments { get; set; } = [];
}

public sealed class BatchWarning
{
    public required string Code { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Headers { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SheetCount { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Mode { get; init; }
}

public sealed class ImportBatchDto
{
    public required Guid Id { get; init; }
    public required Guid ProjectId { get; init; }

    [JsonConverter(typeof(ImportEnumConverter))]
    public required ImportSource Source { get; init; }

    [JsonConverter(typeof(ImportEnumConverter))]
    public required ImportBatchStatus Status { get; init; }
    public required string OriginalFileName { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required Guid CreatedById { get; init; }
    public Guid? FallbackColumnId { get; init; }
    public required IReadOnlyList<string> Headers { get; init; }
    public required IReadOnlyList<IReadOnlyList<string?>> SampleRows { get; init; }
    public ColumnMapping? SuggestedMapping { get; init; }
    public ColumnMapping? Mapping { get; init; }
    public required IReadOnlyList<BatchWarning> Warnings { get; init; }
}

public sealed class UpdateImportMappingRequest
{
    public Guid? FallbackColumnId { get; set; }
    public List<ColumnAssignment>? Assignments { get; set; }

    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? ExtensionData { get; set; }
}

public sealed class RowProblem
{
    public required string Code { get; init; }
    public int? MaxLength { get; init; }
    public int? MaxChildren { get; init; }
}

public sealed class ImportRowDto
{
    public required int RowNumber { get; init; }
    public string? Title { get; init; }
    public string? ExternalId { get; init; }

    [JsonConverter(typeof(ImportEnumConverter))]
    public required ImportRowStatus Status { get; init; }
    public Guid? TaskId { get; init; }
    public string? DisplayKey { get; init; }
    public Guid? ColumnId { get; init; }
    public string? ColumnName { get; init; }
    public Guid? AssigneeId { get; init; }
    public string? ParentExternalId { get; init; }
    public RowProblem? Error { get; init; }
    public RowProblem? Warning { get; init; }
}

public sealed class ImportPreviewDto
{
    public required ImportBatchDto Batch { get; init; }
    public required IReadOnlyList<ImportRowDto> Rows { get; init; }
}

public sealed class CommitImportRequest
{
    public bool? SkipInvalidRows { get; set; }
}

public sealed class ImportCommitDto
{
    public required Guid BatchId { get; init; }

    [JsonConverter(typeof(ImportEnumConverter))]
    public required ImportBatchStatus Status { get; init; }

    public required int CreatedCount { get; init; }
    public required int SkippedExistingCount { get; init; }
    public required int SkippedArchivedCount { get; init; }
    public required int SkippedInvalidCount { get; init; }
    public required int SkippedSourceArchivedCount { get; init; }
    public required int LinkedCount { get; init; }
    public required int UnlinkedCount { get; init; }
    public required IReadOnlyList<ImportRowDto> Rows { get; init; }
}

public sealed class ImportUndoKeptDto
{
    public required Guid TaskId { get; init; }
    public required string DisplayKey { get; init; }

    [JsonConverter(typeof(ImportEnumConverter))]
    public required ImportUndoReason Reason { get; init; }
}

public sealed class ImportUndoDto
{
    public required Guid BatchId { get; init; }

    [JsonConverter(typeof(ImportEnumConverter))]
    public required ImportBatchStatus Status { get; init; }

    public required int ArchivedCount { get; init; }
    public required IReadOnlyList<ImportUndoKeptDto> Kept { get; init; }
}

public sealed class ExportTasksRequest
{
    public string? Format { get; set; }
}

public sealed class ImportCompletedDto
{
    public required Guid ProjectId { get; init; }
    public required Guid ImportId { get; init; }
    public required int CreatedCount { get; init; }
    public required int SkippedCount { get; init; }
    public required bool Refetch { get; init; }
}

public sealed class ImportUndoneDto
{
    public required Guid ProjectId { get; init; }
    public required Guid ImportId { get; init; }
    public required IReadOnlyList<Guid> ArchivedTaskIds { get; init; }
    public required bool Refetch { get; init; }
}

public sealed class ProjectExportFile
{
    public required byte[] Content { get; init; }
    public required string ContentType { get; init; }
    public required string FileName { get; init; }
}
