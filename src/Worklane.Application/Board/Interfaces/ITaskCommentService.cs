using Worklane.Application.Board.Dtos;

namespace Worklane.Application.Board.Interfaces;

public interface ITaskCommentService
{
    Task<IReadOnlyList<CommentDto>> GetCommentsAsync(Guid taskId, CancellationToken cancellationToken = default);

    Task<CommentDto> CreateCommentAsync(Guid taskId, CreateCommentRequest request, Guid userId, CancellationToken cancellationToken = default);

    Task<CommentDto> UpdateCommentAsync(Guid commentId, UpdateCommentRequest request, Guid userId, CancellationToken cancellationToken = default);

    Task DeleteCommentAsync(Guid commentId, Guid userId, CancellationToken cancellationToken = default);

    Task<Guid?> GetWorkspaceIdForCommentAsync(Guid commentId, CancellationToken cancellationToken = default);
}
