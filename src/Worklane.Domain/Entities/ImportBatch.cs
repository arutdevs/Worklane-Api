using Worklane.Domain.Enums;

namespace Worklane.Domain.Entities;

/// <summary>
/// One uploaded import file for a project. Tenant scope is the project's workspace;
/// this row has no WorkspaceId of its own.
/// </summary>
public class ImportBatch
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid CreatedById { get; set; }
    public ImportSource Source { get; set; }
    public ImportBatchStatus Status { get; set; }
    public string OriginalFileName { get; set; } = string.Empty;
    public string? StorageKey { get; set; }
    public DateTimeOffset? FileDeletedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Column used when a status cell matches nothing. No FK: column delete must stay possible.</summary>
    public Guid? FallbackColumnId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public string HeadersJson { get; set; } = "[]";
    public string? SampleJson { get; set; }
    public string? SuggestedMappingJson { get; set; }
    public string? MappingJson { get; set; }
    public string? WarningsJson { get; set; }

    public Project Project { get; set; } = null!;
    public ICollection<ImportBatchRow> Rows { get; set; } = new List<ImportBatchRow>();
    public ICollection<TaskItem> Tasks { get; set; } = new List<TaskItem>();
}
