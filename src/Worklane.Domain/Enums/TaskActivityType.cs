namespace Worklane.Domain.Enums;

/// <summary>
/// What a <see cref="Entities.TaskActivity"/> row records. Stored as a string.
/// There is no database default: the first member is 0, and a default would make EF
/// drop an explicit <see cref="Created"/> from the INSERT.
/// </summary>
public enum TaskActivityType
{
    Created,
    Moved,
    Updated,
    CommentAdded,
    CommentEdited,
    CommentDeleted,
    AttachmentAdded,
    AttachmentDeleted
}
