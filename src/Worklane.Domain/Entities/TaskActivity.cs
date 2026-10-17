using Worklane.Domain.Enums;

namespace Worklane.Domain.Entities;

/// <summary>
/// One recorded event in a task's history. Column moves were the original rows
/// (<see cref="FromColumnId"/> / <see cref="ToColumnId"/>); field edits, comments
/// and attachments are stored in <see cref="Details"/> as a JSON object.
/// </summary>
public class TaskActivity
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    public TaskActivityType Type { get; set; }

    /// <summary>Set for <see cref="TaskActivityType.Moved"/>. Null for every other event.</summary>
    public Guid? FromColumnId { get; set; }

    /// <summary>Set for <see cref="TaskActivityType.Created"/> and <see cref="TaskActivityType.Moved"/>.</summary>
    public Guid? ToColumnId { get; set; }

    public Guid ActorUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Event-specific JSON object. Null only for legacy rows that could not be backfilled.
    /// Moved: fromColumnId, fromColumnName, toColumnId, toColumnName.
    /// Updated: field, oldValue, newValue — one row per changed field.
    /// </summary>
    public string? Details { get; set; }

    public TaskItem Task { get; set; } = null!;
}
