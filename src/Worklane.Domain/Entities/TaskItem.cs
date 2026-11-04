using Worklane.Domain.Enums;

namespace Worklane.Domain.Entities;

public class TaskItem
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid ColumnId { get; set; }

    /// <summary>Sequential per-project counter used in the display key (e.g. "DEMO-14").</summary>
    public int Number { get; set; }

    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Guid? AssigneeId { get; set; }
    public TaskPriority Priority { get; set; }
    public DateTimeOffset? DueDate { get; set; }

    /// <summary>
    /// Fractional ordering within the column. Values start at 65536 and are halved
    /// on insertion; the column is rebalanced when adjacent gap falls below 0.001.
    /// </summary>
    public double Order { get; set; }

    /// <summary>Planned effort in man-days. Null when the task has not been estimated.</summary>
    public double? EstimateDays { get; set; }

    /// <summary>First time the task entered a column flagged <see cref="BoardColumn.IsInProgress"/>.</summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>
    /// Most recent time the task entered a column flagged <see cref="BoardColumn.IsDone"/>.
    /// Cleared when the task is dragged back out of a done column.
    /// </summary>
    public DateTimeOffset? CompletedAt { get; set; }

    public Guid CreatedById { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public bool IsArchived { get; set; }

    /// <summary>Optional parent in the same project. Null when this card is not a subtask.</summary>
    public Guid? ParentTaskId { get; set; }

    /// <summary>Set only on cards created by an import. Null on every other card.</summary>
    public ImportSource? ImportSource { get; set; }

    /// <summary>Source-system id. Unique per project and <see cref="ImportSource"/>, including archived cards.</summary>
    public string? ExternalId { get; set; }

    /// <summary>Batch that created this card. Null when the card was not imported.</summary>
    public Guid? ImportBatchId { get; set; }

    public Project Project { get; set; } = null!;
    public BoardColumn Column { get; set; } = null!;
    public TaskItem? Parent { get; set; }
    public ICollection<TaskItem> Children { get; set; } = new List<TaskItem>();
    public ImportBatch? ImportBatch { get; set; }
}
