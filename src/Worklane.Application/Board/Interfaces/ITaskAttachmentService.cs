using Worklane.Application.Board.Dtos;

namespace Worklane.Application.Board.Interfaces;

public interface ITaskAttachmentService
{
    Task<IReadOnlyList<AttachmentDto>> GetAttachmentsAsync(Guid taskId, CancellationToken cancellationToken = default);

    Task<AttachmentDto> UploadAsync(
        Guid taskId,
        string fileName,
        string? contentType,
        Stream content,
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>Opens the stored bytes. The caller owns the returned stream.</summary>
    Task<AttachmentContent> OpenAsync(Guid attachmentId, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid attachmentId, Guid userId, CancellationToken cancellationToken = default);

    Task<Guid?> GetWorkspaceIdForAttachmentAsync(Guid attachmentId, CancellationToken cancellationToken = default);
}

public sealed record AttachmentContent(Stream Content, string ContentType, string FileName);
