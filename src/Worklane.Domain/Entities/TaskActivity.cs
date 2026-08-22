namespace Worklane.Domain.Entities;

/// <summary>
/// One recorded move of a task between columns. Kept as history because
/// <see cref="TaskItem.StartedAt"/> and <see cref="TaskItem.CompletedAt"/> only hold
/// the latest state — a task dragged out of Done and finished again would otherwise
/// lose the earlier pass, and time reports would be wrong.
/// </summary>
public class TaskActivity
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }

    /// <summary>Null for the very first placement, when the task was created.</summary>
    public Guid? FromColumnId { get; set; }
    public Guid ToColumnId { get; set; }

    public Guid MovedByUserId { get; set; }
    public DateTimeOffset MovedAt { get; set; }

    public TaskItem Task { get; set; } = null!;
}
