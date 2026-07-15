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

    public Guid CreatedById { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public bool IsArchived { get; set; }

    public Project Project { get; set; } = null!;
    public BoardColumn Column { get; set; } = null!;
}
