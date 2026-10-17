using Microsoft.EntityFrameworkCore;
using Worklane.Application.Board.Dtos;
using Worklane.Application.Board.Interfaces;
using Worklane.Application.Common.Exceptions;
using Worklane.Application.Workspaces.Interfaces;
using Worklane.Domain.Entities;
using Worklane.Domain.Enums;
using Worklane.Infrastructure.Persistence;

namespace Worklane.Infrastructure.Services;

public class TaskCommentService : ITaskCommentService
{
    private readonly WorklaneDbContext _db;
    private readonly IBoardNotifier _notifier;
    private readonly IWorkspaceService _workspaces;

    public TaskCommentService(WorklaneDbContext db, IBoardNotifier notifier, IWorkspaceService workspaces)
    {
        _db = db;
        _notifier = notifier;
        _workspaces = workspaces;
    }

    public async Task<IReadOnlyList<CommentDto>> GetCommentsAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var exists = await _db.TaskItems.AnyAsync(t => t.Id == taskId, cancellationToken);
        if (!exists)
            throw new NotFoundException(nameof(TaskItem), taskId);

        var comments = await _db.TaskComments
            .Where(c => c.TaskId == taskId)
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .ToListAsync(cancellationToken);

        var names = await UserLookup.DisplayNamesAsync(_db, comments.Select(c => c.AuthorId), cancellationToken);
        return comments.Select(c => ToDto(c, names)).ToList();
    }

    public async Task<CommentDto> CreateCommentAsync(
        Guid taskId, CreateCommentRequest request, Guid userId, CancellationToken cancellationToken = default)
    {
        var task = await _db.TaskItems
            .Include(t => t.Project)
            .FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken)
            ?? throw new NotFoundException(nameof(TaskItem), taskId);

        var now = DateTimeOffset.UtcNow;
        var comment = new TaskComment
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            AuthorId = userId,
            Body = request.Body.Trim(),
            CreatedAt = now,
            UpdatedAt = null
        };

        _db.TaskComments.Add(comment);
        _db.TaskActivities.Add(new TaskActivity
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            Type = TaskActivityType.CommentAdded,
            ActorUserId = userId,
            CreatedAt = now,
            Details = ActivityJson.Of(new { commentId = comment.Id, body = comment.Body })
        });
        await _db.SaveChangesAsync(cancellationToken);

        var names = await UserLookup.DisplayNamesAsync(_db, [userId], cancellationToken);
        var dto = ToDto(comment, names);
        await _notifier.CommentCreatedAsync(task.ProjectId, Broadcast(dto, task.ProjectId), cancellationToken);
        return dto;
    }

    public async Task<CommentDto> UpdateCommentAsync(
        Guid commentId, UpdateCommentRequest request, Guid userId, CancellationToken cancellationToken = default)
    {
        var comment = await _db.TaskComments
            .Include(c => c.Task)
            .FirstOrDefaultAsync(c => c.Id == commentId, cancellationToken)
            ?? throw new NotFoundException(nameof(TaskComment), commentId);

        if (comment.AuthorId != userId)
            throw new ForbiddenAccessException("Only the author can edit this comment.");

        var body = request.Body.Trim();
        if (body == comment.Body)
        {
            var currentNames = await UserLookup.DisplayNamesAsync(_db, [comment.AuthorId], cancellationToken);
            return ToDto(comment, currentNames);
        }

        var previous = comment.Body;
        var now = DateTimeOffset.UtcNow;
        comment.Body = body;
        comment.UpdatedAt = now;
        _db.TaskActivities.Add(new TaskActivity
        {
            Id = Guid.NewGuid(),
            TaskId = comment.TaskId,
            Type = TaskActivityType.CommentEdited,
            ActorUserId = userId,
            CreatedAt = now,
            Details = ActivityJson.Of(new { commentId = comment.Id, oldValue = previous, newValue = body })
        });
        await _db.SaveChangesAsync(cancellationToken);

        var names = await UserLookup.DisplayNamesAsync(_db, [comment.AuthorId], cancellationToken);
        var dto = ToDto(comment, names);
        await _notifier.CommentUpdatedAsync(comment.Task.ProjectId, Broadcast(dto, comment.Task.ProjectId), cancellationToken);
        return dto;
    }

    public async Task DeleteCommentAsync(Guid commentId, Guid userId, CancellationToken cancellationToken = default)
    {
        var comment = await _db.TaskComments
            .Include(c => c.Task)
            .ThenInclude(t => t.Project)
            .FirstOrDefaultAsync(c => c.Id == commentId, cancellationToken)
            ?? throw new NotFoundException(nameof(TaskComment), commentId);

        if (!await CanModerateAsync(comment.Task.Project.WorkspaceId, comment.AuthorId, userId, cancellationToken))
            throw new ForbiddenAccessException("Only the author or a workspace admin can delete this comment.");

        var projectId = comment.Task.ProjectId;
        var taskId = comment.TaskId;
        var now = DateTimeOffset.UtcNow;

        _db.TaskComments.Remove(comment);
        _db.TaskActivities.Add(new TaskActivity
        {
            Id = Guid.NewGuid(),
            TaskId = taskId,
            Type = TaskActivityType.CommentDeleted,
            ActorUserId = userId,
            CreatedAt = now,
            Details = ActivityJson.Of(new { commentId })
        });
        await _db.SaveChangesAsync(cancellationToken);

        await _notifier.CommentDeletedAsync(
            projectId,
            new CommentDeletedEvent(projectId, taskId, commentId),
            cancellationToken);
    }

    public async Task<Guid?> GetWorkspaceIdForCommentAsync(Guid commentId, CancellationToken cancellationToken = default)
    {
        return await _db.TaskComments
            .IgnoreQueryFilters()
            .Where(c => c.Id == commentId)
            .Select(c => (Guid?)c.Task.Project.WorkspaceId)
            .SingleOrDefaultAsync(cancellationToken);
    }

    private async Task<bool> CanModerateAsync(Guid workspaceId, Guid authorId, Guid userId, CancellationToken cancellationToken)
    {
        if (authorId == userId) return true;
        var role = await _workspaces.GetUserRoleAsync(workspaceId, userId, cancellationToken);
        return role is WorkspaceRole.Admin or WorkspaceRole.Owner;
    }

    private static CommentDto ToDto(TaskComment comment, IReadOnlyDictionary<Guid, string> names) =>
        new(comment.Id, comment.TaskId, UserLookup.Ref(names, comment.AuthorId), comment.Body, comment.CreatedAt, comment.UpdatedAt);

    private static CommentBroadcastDto Broadcast(CommentDto dto, Guid projectId) =>
        new(dto.Id, dto.TaskId, dto.Author, dto.Body, dto.CreatedAt, dto.UpdatedAt, projectId);
}
