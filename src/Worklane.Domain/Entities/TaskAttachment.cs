namespace Worklane.Domain.Entities;

public class TaskAttachment
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    public Guid UploadedById { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }

    /// <summary>Key understood by <c>IFileStorage</c>. Never taken from the client.</summary>
    public string StorageKey { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public TaskItem Task { get; set; } = null!;
}
