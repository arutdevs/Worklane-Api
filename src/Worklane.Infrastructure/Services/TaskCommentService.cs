using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
    private readonly IBoardService _board;
    private readonly IWorkspaceService _workspaces;
    private readonly ILogger<TaskCommentService> _logger;

    public TaskCommentService(
        WorklaneDbContext db,
        IBoardNotifier notifier,
        IBoardService board,
        IWorkspaceService workspaces,
        ILogger<TaskCommentService> logger)
    {
        _db = db;
        _notifier = notifier;
        _board = board;
        _workspaces = workspaces;
        _logger = logger;
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
        await BroadcastQuietlyAsync(async () =>
        {
            await _notifier.CommentCreatedAsync(task.ProjectId, Broadcast(dto, task.ProjectId), cancellationToken);
            await BroadcastTaskCountsAsync(task.Id, cancellationToken);
        }, cancellationToken);
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
            Details = ActivityJson.Of(new { commentId = comment.Id, edited = true })
        });
        await _db.SaveChangesAsync(cancellationToken);

        var names = await UserLookup.DisplayNamesAsync(_db, [comment.AuthorId], cancellationToken);
        var dto = ToDto(comment, names);
        // The count does not change on edit, so there is no TaskUpdated broadcast.
        await BroadcastQuietlyAsync(
            () => _notifier.CommentUpdatedAsync(comment.Task.ProjectId, Broadcast(dto, comment.Task.ProjectId), cancellationToken),
            cancellationToken);
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

        var history = await _db.TaskActivities
            .Where(a => a.TaskId == taskId &&
                (a.Type == TaskActivityType.CommentAdded || a.Type == TaskActivityType.CommentEdited))
            .ToListAsync(cancellationToken);
        foreach (var activity in history)
            RedactCommentText(activity, commentId);

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

        await BroadcastQuietlyAsync(async () =>
        {
            await _notifier.CommentDeletedAsync(
                projectId,
                new CommentDeletedEvent(projectId, taskId, commentId),
                cancellationToken);
            await BroadcastTaskCountsAsync(taskId, cancellationToken);
        }, cancellationToken);
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
        var role = await _workspaces.GetUserRoleAsync(workspaceId, userId, cancellationToken);
        if (role is WorkspaceRole.Admin or WorkspaceRole.Owner)
            return true;
        return authorId == userId && role == WorkspaceRole.Member;
    }

    private async Task BroadcastQuietlyAsync(Func<Task> broadcast, CancellationToken cancellationToken)
    {
        try
        {
            await broadcast();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Comment change was saved but could not be broadcast.");
        }
    }

    private static CommentDto ToDto(TaskComment comment, IReadOnlyDictionary<Guid, string> names) =>
        new(comment.Id, comment.TaskId, UserLookup.Ref(names, comment.AuthorId), comment.Body, comment.CreatedAt, comment.UpdatedAt);

    private async Task BroadcastTaskCountsAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var task = await _board.GetTaskAsync(taskId, cancellationToken);
        await _notifier.TaskUpdatedAsync(task.ProjectId, task, cancellationToken);
    }

    /// <summary>
    /// Drops body, oldValue, and newValue from activity rows for this comment so a
    /// Viewer reading the log cannot recover text that was deleted.
    /// </summary>
    private static void RedactCommentText(TaskActivity activity, Guid commentId)
    {
        if (string.IsNullOrEmpty(activity.Details)) return;

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(activity.Details);
        }
        catch (System.Text.Json.JsonException)
        {
            return;
        }

        if (node is not JsonObject obj) return;
        if (!Guid.TryParse(obj["commentId"]?.ToString(), out var id) || id != commentId) return;

        obj.Remove("body");
        obj.Remove("oldValue");
        obj.Remove("newValue");
        activity.Details = obj.ToJsonString(ActivityJson.Options);
    }

    private static CommentBroadcastDto Broadcast(CommentDto dto, Guid projectId) =>
        new(dto.Id, dto.TaskId, dto.Author, dto.Body, dto.CreatedAt, dto.UpdatedAt, projectId);
}
