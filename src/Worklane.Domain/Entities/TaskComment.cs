namespace Worklane.Domain.Entities;

public class TaskComment
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    public Guid AuthorId { get; set; }
    public string Body { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Null until the author edits the comment.</summary>
    public DateTimeOffset? UpdatedAt { get; set; }

    public TaskItem Task { get; set; } = null!;
}
