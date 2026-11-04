using Worklane.Domain.Enums;

namespace Worklane.Domain.Entities;

/// <summary>One data row of an import. The header is not a row. <see cref="RowNumber"/> is 1-based.</summary>
public class ImportBatchRow
{
    public Guid Id { get; set; }
    public Guid ImportBatchId { get; set; }
    public int RowNumber { get; set; }
    public string? ExternalId { get; set; }
    public string? ParentExternalId { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? ColumnName { get; set; }
    public string? AssigneeEmail { get; set; }
    public string? PriorityText { get; set; }
    public string? DueDateText { get; set; }
    public string? EstimateText { get; set; }
    public ImportRowStatus Status { get; set; }
    public string? ErrorCode { get; set; }
    public string? WarningCode { get; set; }
    public Guid? TaskId { get; set; }

    public ImportBatch ImportBatch { get; set; } = null!;
    public TaskItem? Task { get; set; }
}
