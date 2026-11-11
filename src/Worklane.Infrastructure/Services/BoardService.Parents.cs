using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Worklane.Application.Board;
using Worklane.Application.Board.Dtos;
using Worklane.Application.Common.Exceptions;
using Worklane.Domain.Entities;
using Worklane.Domain.Enums;
using Worklane.Infrastructure.Persistence;

namespace Worklane.Infrastructure.Services;

public partial class BoardService
{
    private readonly record struct ChildProgress(int ChildCount, int DoneChildCount);

    private readonly record struct LinkOutcome(TaskItemDto Child, TaskItemDto? Parent, bool Changed);

    private readonly record struct ArchiveBroadcast(IReadOnlyList<TaskArchivedEvent> Archived, Guid? SurvivingParentId);

    private sealed class ParentLinkSnapshot
    {
        public static ParentLinkSnapshot Empty { get; } = new(
            new Dictionary<Guid, string>(),
            new Dictionary<Guid, ChildProgress>());

        private readonly IReadOnlyDictionary<Guid, string> _displayKeys;
        private readonly IReadOnlyDictionary<Guid, ChildProgress> _progress;

        public ParentLinkSnapshot(
            IReadOnlyDictionary<Guid, string> displayKeys,
            IReadOnlyDictionary<Guid, ChildProgress> progress)
        {
            _displayKeys = displayKeys;
            _progress = progress;
        }

        public (Guid? ParentTaskId, string? ParentDisplayKey, int ChildCount, int DoneChildCount) For(TaskItem task)
        {
            _progress.TryGetValue(task.Id, out var progress);
            if (task.ParentTaskId is not Guid parentId)
                return (null, null, progress.ChildCount, progress.DoneChildCount);

            if (!_displayKeys.TryGetValue(parentId, out var key))
                throw new InvalidOperationException("Parent display key was not loaded for the task.");

            return (parentId, key, progress.ChildCount, progress.DoneChildCount);
        }
    }

    public async Task<TaskItemDto> SetParentAsync(Guid taskId, Guid parentTaskId, Guid userId, CancellationToken ct = default)
    {
        // Same id is rejected before any parent lookup so a missing id and a self id stay distinct.
        if (taskId == parentTaskId)
            throw new BadRequestException(ParentTaskErrors.ParentSelf, ParentTaskErrors.ParentSelfDetail);

        var outcome = await InLinkTransactionAsync(async token =>
        {
            var workspaceId = RequireWorkspaceId();
            var locked = await LockIdsAsync(workspaceId, [taskId, parentTaskId], token);
            var rows = await LoadLockedAsync(locked, workspaceId, token);
            var child = rows.FirstOrDefault(t => t.Id == taskId);
            var parent = rows.FirstOrDefault(t => t.Id == parentTaskId);
            // Missing and other-workspace parents both fail this lookup. The body is the same.
            if (child is null || parent is null)
                throw new NotFoundException(ParentTaskErrors.NotFoundDetail);

            if (await RejectOrUnchangedAsync(parent, child.ProjectId, child.IsArchived, child.ParentTaskId, child.Id, token))
            {
                return new LinkOutcome(await MapTaskAsync(child, child.Project.Key, token), null, false);
            }

            var now = DateTimeOffset.UtcNow;
            child.ParentTaskId = parent.Id;
            child.UpdatedAt = now;
            AddLinkActivities(child, parent, userId, now);
            await _db.SaveChangesAsync(token);
            return new LinkOutcome(
                await MapTaskAsync(child, child.Project.Key, token),
                await MapTaskAsync(parent, parent.Project.Key, token),
                true);
        }, ct);

        return await BroadcastLinkAsync(outcome, ct);
    }

    public async Task<TaskItemDto> ClearParentAsync(Guid taskId, Guid userId, CancellationToken ct = default)
    {
        var outcome = await InLinkTransactionAsync(async token =>
        {
            var workspaceId = RequireWorkspaceId();
            var locked = await LockChildAndParentAsync(workspaceId, taskId, token);
            var rows = await LoadLockedAsync(locked, workspaceId, token);
            var child = rows.FirstOrDefault(t => t.Id == taskId)
                ?? throw new NotFoundException(nameof(TaskItem), taskId);

            if (child.ParentTaskId is not Guid parentId)
                return new LinkOutcome(await MapTaskAsync(child, child.Project.Key, token), null, false);

            var parent = rows.FirstOrDefault(t => t.Id == parentId);
            var now = DateTimeOffset.UtcNow;
            var childKey = DisplayKey(child.Project.Key, child.Number);
            var parentKey = parent is null
                ? childKey
                : DisplayKey(parent.Project.Key, parent.Number);

            child.ParentTaskId = null;
            child.UpdatedAt = now;
            AddParentActivity(child.Id, TaskActivityType.ParentCleared, parentId, parentKey, userId, now);
            if (parent is not null)
                AddParentActivity(parent.Id, TaskActivityType.ChildRemoved, child.Id, childKey, userId, now);

            await _db.SaveChangesAsync(token);
            return new LinkOutcome(
                await MapTaskAsync(child, child.Project.Key, token),
                parent is null ? null : await MapTaskAsync(parent, parent.Project.Key, token),
                true);
        }, ct);

        return await BroadcastLinkAsync(outcome, ct);
    }

    public async Task<IReadOnlyList<TaskItemDto>> GetChildrenAsync(Guid taskId, CancellationToken ct = default)
    {
        var children = await _db.TaskItems
            .Where(t => t.ParentTaskId == taskId)
            .OrderBy(t => t.CreatedAt)
            .ThenBy(t => t.Number)
            .ToListAsync(ct);

        if (children.Count == 0)
            return [];

        var projectKey = await _db.Projects
            .Where(p => p.Id == children[0].ProjectId)
            .Select(p => p.Key)
            .FirstAsync(ct);
        var assigneeMap = await BuildAssigneeMapAsync(children, ct);
        var wip = await LoadWipAsync(children.Select(t => t.ColumnId), ct);
        var cardCounts = await LoadCardCountsAsync(children.Select(t => t.Id), ct);
        var links = await LoadParentLinksAsync(children, ct);
        return children
            .Select(t => ToDto(t, projectKey, assigneeMap, Over(wip, t.ColumnId), cardCounts, links))
            .ToList();
    }

    public async Task ArchiveTaskAsync(Guid taskId, bool includeChildren, Guid userId, CancellationToken ct = default)
    {
        // Broadcast only after commit. A rejected archive throws inside the transaction.
        var broadcast = await InLinkTransactionAsync(async token =>
        {
            var workspaceId = RequireWorkspaceId();
            var locked = await LockFamilyAsync(workspaceId, taskId, token);
            var rows = await LoadLockedAsync(locked, workspaceId, token);
            var task = rows.FirstOrDefault(t => t.Id == taskId);
            if (task is null || task.IsArchived)
                throw new NotFoundException(nameof(TaskItem), taskId);

            var activeChildren = rows
                .Where(t => t.Id != task.Id && t.ParentTaskId == task.Id && !t.IsArchived)
                .ToList();
            if (activeChildren.Count > 0 && !includeChildren)
            {
                throw new ConflictException(
                    ParentTaskErrors.ParentHasActiveChildren,
                    string.Format(CultureInfo.InvariantCulture, ParentTaskErrors.ParentHasActiveChildrenDetail, activeChildren.Count),
                    ErrorParams.Of((ParentTaskErrors.ChildCount, activeChildren.Count)));
            }

            var now = DateTimeOffset.UtcNow;
            ArchiveCard(task, userId, now);
            foreach (var child in activeChildren)
                ArchiveCard(child, userId, now);

            // Root first, then each child this call archived, ordered by id. Already-archived children stay out.
            var archived = new List<TaskArchivedEvent>(1 + activeChildren.Count)
            {
                new(task.ProjectId, task.Id, task.ColumnId, task.ParentTaskId)
            };
            foreach (var child in activeChildren.OrderBy(child => child.Id))
                archived.Add(new(child.ProjectId, child.Id, child.ColumnId, child.ParentTaskId));

            // The parent stays on the board only when this card is a child and is archived alone.
            Guid? survivingParentId = activeChildren.Count == 0 && task.ParentTaskId is Guid parentId
                ? parentId
                : null;

            await _db.SaveChangesAsync(token);
            return new ArchiveBroadcast(archived, survivingParentId);
        }, ct);

        foreach (var archived in broadcast.Archived)
            await _notifier.TaskArchivedAsync(archived.ProjectId, archived, ct);

        if (broadcast.SurvivingParentId is Guid notifyId)
            await NotifyParentCountsAsync(notifyId, ct);
    }

    private async Task<TaskItemDto> CreateLinkedTaskAsync(
        Project project,
        BoardColumn column,
        CreateTaskRequest request,
        Guid userId,
        Guid parentTaskId,
        CancellationToken ct)
    {
        var created = await InLinkTransactionAsync(async token =>
        {
            var workspaceId = RequireWorkspaceId();
            var locked = await LockIdsAsync(workspaceId, [parentTaskId], token);
            var parent = (await LoadLockedAsync(locked, workspaceId, token)).FirstOrDefault(t => t.Id == parentTaskId)
                ?? throw new NotFoundException(ParentTaskErrors.NotFoundDetail);

            await RejectOrUnchangedAsync(parent, project.Id, childIsArchived: false, currentParentId: null, childId: null, token);

            var now = DateTimeOffset.UtcNow;
            var task = NewTask(
                project.Id,
                column,
                request,
                userId,
                await NextTaskNumberAsync(project.Id, token),
                await NextOrderAsync(column.Id, token),
                now,
                parentTaskId);
            _db.TaskItems.Add(task);
            AddCreatedActivity(task, column, userId, now);
            AddLinkActivities(task, parent, userId, now);
            await _db.SaveChangesAsync(token);
            return (
                Child: await MapTaskAsync(task, project.Key, token),
                Parent: await MapTaskAsync(parent, parent.Project.Key, token));
        }, ct);

        await _notifier.TaskCreatedAsync(project.Id, created.Child, ct);
        await _notifier.TaskUpdatedAsync(created.Parent.ProjectId, created.Parent, ct);
        return created.Child;
    }

    /// <summary>
    /// One read-committed transaction. Row locks are taken in id order inside the
    /// statements below. Broadcast happens after this returns.
    /// </summary>
    private async Task<T> InLinkTransactionAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        return await DbRetry.ExecuteAsync(_db, async token =>
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
            try
            {
                var result = await action(token);
                await transaction.CommitAsync(token);
                return result;
            }
            catch (Exception ex) when (ex is not AppException)
            {
                if (ParentLinkConflicts.Translate(ex) is { } mapped)
                {
                    _logger.LogWarning(ex, "Parent link rejected as {Code}", mapped.Code);
                    throw mapped;
                }

                throw;
            }
        }, ct);
    }

    /// <returns>True when the child is already under this parent and nothing should be written.</returns>
    private async Task<bool> RejectOrUnchangedAsync(
        TaskItem parent,
        Guid childProjectId,
        bool childIsArchived,
        Guid? currentParentId,
        Guid? childId,
        CancellationToken ct)
    {
        if (parent.IsArchived || childIsArchived)
            throw new ConflictException(ParentTaskErrors.CardArchived, ParentTaskErrors.CardArchivedDetail);

        if (parent.ProjectId != childProjectId)
            throw new ConflictException(ParentTaskErrors.ParentDifferentProject, ParentTaskErrors.ParentDifferentProjectDetail);

        if (parent.ParentTaskId is not null)
            throw new ConflictException(ParentTaskErrors.ParentDepthExceeded, ParentTaskErrors.ParentDepthExceededDetail);

        if (childId is Guid id)
        {
            var workspaceId = RequireWorkspaceId();
            // Archived children still occupy a level. The count is every child row.
            var children = await _db.TaskItems
                .IgnoreQueryFilters()
                .CountAsync(t => t.ParentTaskId == id && t.Project.WorkspaceId == workspaceId, ct);
            if (children > 0)
            {
                throw new ConflictException(
                    ParentTaskErrors.ChildHasChildren,
                    ParentTaskErrors.ChildHasChildrenDetail,
                    ErrorParams.Of((ParentTaskErrors.ChildCount, children)));
            }

            if (currentParentId == parent.Id)
                return true;

            if (currentParentId is Guid existingParentId)
            {
                var key = await DisplayKeyAsync(existingParentId, workspaceId, ct);
                throw new ConflictException(
                    ParentTaskErrors.AlreadyHasParent,
                    string.Format(CultureInfo.InvariantCulture, ParentTaskErrors.AlreadyHasParentDetail, key),
                    ErrorParams.Of((ParentTaskErrors.ParentDisplayKey, key)));
            }
        }

        var active = await _db.TaskItems.CountAsync(t => t.ParentTaskId == parent.Id, ct);
        if (active >= _maxChildren)
        {
            throw new ConflictException(
                ParentTaskErrors.ParentChildLimit,
                string.Format(CultureInfo.InvariantCulture, ParentTaskErrors.ParentChildLimitDetail, _maxChildren),
                ErrorParams.Of((ParentTaskErrors.MaxChildren, _maxChildren)));
        }

        return false;
    }

    private async Task<TaskItemDto> BroadcastLinkAsync(LinkOutcome outcome, CancellationToken ct)
    {
        if (!outcome.Changed)
            return outcome.Child;

        await _notifier.TaskUpdatedAsync(outcome.Child.ProjectId, outcome.Child, ct);
        if (outcome.Parent is not null)
            await _notifier.TaskUpdatedAsync(outcome.Parent.ProjectId, outcome.Parent, ct);
        return outcome.Child;
    }

    private async Task NotifyParentCountsAsync(Guid parentId, CancellationToken ct)
    {
        var parent = await _db.TaskItems
            .Include(t => t.Project)
            .FirstOrDefaultAsync(t => t.Id == parentId, ct);
        if (parent is null)
            return;

        var dto = await MapTaskAsync(parent, parent.Project.Key, ct);
        await _notifier.TaskUpdatedAsync(dto.ProjectId, dto, ct);
    }

    private async Task<ParentLinkSnapshot> LoadParentLinksAsync(IReadOnlyCollection<TaskItem> tasks, CancellationToken ct)
    {
        if (tasks.Count == 0 || _tenant.WorkspaceId is not Guid workspaceId)
            return ParentLinkSnapshot.Empty;

        var projectIds = tasks.Select(t => t.ProjectId).Distinct().ToList();
        // One grouped query for the projects on this response, not one query per card.
        var progress = await _db.TaskItems
            .Where(t => projectIds.Contains(t.ProjectId) && t.ParentTaskId != null)
            .GroupBy(t => t.ParentTaskId)
            .Select(g => new
            {
                ParentId = g.Key,
                ChildCount = g.Count(),
                DoneChildCount = g.Count(x => x.Column.IsDone)
            })
            .ToListAsync(ct);

        var parentIds = tasks
            .Where(t => t.ParentTaskId.HasValue)
            .Select(t => t.ParentTaskId!.Value)
            .Distinct()
            .ToList();
        Dictionary<Guid, string> keys = [];
        if (parentIds.Count > 0)
        {
            var rows = await _db.TaskItems
                .IgnoreQueryFilters()
                .Where(t => parentIds.Contains(t.Id) && t.Project.WorkspaceId == workspaceId)
                .Select(t => new { t.Id, t.Number, t.Project.Key })
                .ToListAsync(ct);
            keys = rows.ToDictionary(row => row.Id, row => DisplayKey(row.Key, row.Number));
        }

        return new ParentLinkSnapshot(
            keys,
            progress
                .Where(row => row.ParentId.HasValue)
                .ToDictionary(
                    row => row.ParentId!.Value,
                    row => new ChildProgress(row.ChildCount, row.DoneChildCount)));
    }

    private async Task<string> DisplayKeyAsync(Guid taskId, Guid workspaceId, CancellationToken ct)
    {
        var row = await _db.TaskItems
            .IgnoreQueryFilters()
            .Where(t => t.Id == taskId && t.Project.WorkspaceId == workspaceId)
            .Select(t => new { t.Number, t.Project.Key })
            .FirstAsync(ct);
        return DisplayKey(row.Key, row.Number);
    }

    private Guid RequireWorkspaceId() =>
        _tenant.WorkspaceId ?? throw new ForbiddenAccessException();

    private async Task<List<Guid>> LockIdsAsync(Guid workspaceId, Guid[] ids, CancellationToken ct) =>
        await QueryLockedIdsAsync(
            """
            SELECT t."Id"
            FROM "TaskItems" AS t
            INNER JOIN "Projects" AS p ON p."Id" = t."ProjectId"
            WHERE p."WorkspaceId" = @workspaceId
              AND t."Id" = ANY(@ids)
            ORDER BY t."Id"
            FOR UPDATE OF t
            """,
            command =>
            {
                command.Parameters.Add(new NpgsqlParameter("workspaceId", NpgsqlDbType.Uuid) { Value = workspaceId });
                command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids });
            },
            ct);

    /// <summary>Locks the child and its current parent in id order, in one statement.</summary>
    private async Task<List<Guid>> LockChildAndParentAsync(Guid workspaceId, Guid childId, CancellationToken ct) =>
        await QueryLockedIdsAsync(
            """
            SELECT t."Id"
            FROM "TaskItems" AS t
            INNER JOIN "Projects" AS p ON p."Id" = t."ProjectId"
            WHERE p."WorkspaceId" = @workspaceId
              AND (
                t."Id" = @childId
                OR t."Id" = (
                  SELECT c."ParentTaskId"
                  FROM "TaskItems" AS c
                  INNER JOIN "Projects" AS cp ON cp."Id" = c."ProjectId"
                  WHERE c."Id" = @childId AND cp."WorkspaceId" = @workspaceId
                )
              )
            ORDER BY t."Id"
            FOR UPDATE OF t
            """,
            command =>
            {
                command.Parameters.Add(new NpgsqlParameter("workspaceId", NpgsqlDbType.Uuid) { Value = workspaceId });
                command.Parameters.Add(new NpgsqlParameter("childId", NpgsqlDbType.Uuid) { Value = childId });
            },
            ct);

    /// <summary>Locks the card and every child row, ordered by id, so a concurrent link cannot sneak in.</summary>
    private async Task<List<Guid>> LockFamilyAsync(Guid workspaceId, Guid taskId, CancellationToken ct) =>
        await QueryLockedIdsAsync(
            """
            SELECT t."Id"
            FROM "TaskItems" AS t
            INNER JOIN "Projects" AS p ON p."Id" = t."ProjectId"
            WHERE p."WorkspaceId" = @workspaceId
              AND (t."Id" = @taskId OR t."ParentTaskId" = @taskId)
            ORDER BY t."Id"
            FOR UPDATE OF t
            """,
            command =>
            {
                command.Parameters.Add(new NpgsqlParameter("workspaceId", NpgsqlDbType.Uuid) { Value = workspaceId });
                command.Parameters.Add(new NpgsqlParameter("taskId", NpgsqlDbType.Uuid) { Value = taskId });
            },
            ct);

    private async Task<List<Guid>> QueryLockedIdsAsync(string sql, Action<NpgsqlCommand> bind, CancellationToken ct)
    {
        var connection = (NpgsqlConnection)_db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(sql, connection);
        if (_db.Database.CurrentTransaction?.GetDbTransaction() is not NpgsqlTransaction transaction)
            throw new InvalidOperationException("Parent link lock requires an open transaction.");
        command.Transaction = transaction;
        bind(command);

        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            ids.Add(reader.GetGuid(0));
        return ids;
    }

    private async Task<List<TaskItem>> LoadLockedAsync(IReadOnlyCollection<Guid> ids, Guid workspaceId, CancellationToken ct)
    {
        if (ids.Count == 0)
            return [];

        return await _db.TaskItems
            .IgnoreQueryFilters()
            .Include(t => t.Project)
            .Where(t => ids.Contains(t.Id) && t.Project.WorkspaceId == workspaceId)
            .ToListAsync(ct);
    }

    private async Task<int> NextTaskNumberAsync(Guid projectId, CancellationToken ct)
    {
        // INSERT ... RETURNING is non-composable. SqlQuery<T> reads a column named "Value"
        // and must be materialised without a further LINQ operator such as FirstAsync.
        var numbers = await _db.Database.SqlQuery<int>(
            $"""
            INSERT INTO "ProjectTaskCounters" ("ProjectId", "LastNumber")
            VALUES ({projectId}, 1)
            ON CONFLICT ("ProjectId") DO UPDATE
            SET "LastNumber" = "ProjectTaskCounters"."LastNumber" + 1
            RETURNING "LastNumber" AS "Value"
            """).ToListAsync(ct);
        return numbers[0];
    }

    private async Task<double> NextOrderAsync(Guid columnId, CancellationToken ct)
    {
        var maxOrder = await _db.TaskItems
            .Where(t => t.ColumnId == columnId)
            .MaxAsync(t => (double?)t.Order, ct) ?? 0;
        return maxOrder + 65536.0;
    }

    private static TaskItem NewTask(
        Guid projectId,
        BoardColumn column,
        CreateTaskRequest request,
        Guid userId,
        int number,
        double order,
        DateTimeOffset now,
        Guid? parentTaskId) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        ColumnId = column.Id,
        Number = number,
        Title = request.Title,
        Description = request.Description,
        AssigneeId = request.AssigneeId,
        Priority = request.Priority,
        DueDate = request.DueDate,
        Order = order,
        EstimateDays = request.EstimateDays,
        StartedAt = column.IsInProgress ? now : null,
        CompletedAt = column.IsDone ? now : null,
        CreatedById = userId,
        CreatedAt = now,
        UpdatedAt = now,
        IsArchived = false,
        ArchivedAt = null,
        ParentTaskId = parentTaskId
    };

    /// <summary>
    /// One <see cref="TaskActivityType.Archived"/> row per card this call archives.
    /// The activity query filter hides it while the card is archived; that filter stays as it is.
    /// </summary>
    private void ArchiveCard(TaskItem task, Guid userId, DateTimeOffset now)
    {
        task.IsArchived = true;
        task.ArchivedAt = now;
        task.UpdatedAt = now;
        _db.TaskActivities.Add(new TaskActivity
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            Type = TaskActivityType.Archived,
            ActorUserId = userId,
            CreatedAt = now,
            Details = "{}"
        });
    }

    private void AddCreatedActivity(TaskItem task, BoardColumn column, Guid userId, DateTimeOffset now)
    {
        _db.TaskActivities.Add(new TaskActivity
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            Type = TaskActivityType.Created,
            ToColumnId = column.Id,
            ActorUserId = userId,
            CreatedAt = now,
            Details = ActivityJson.Of(new { columnId = column.Id, columnName = column.Name, title = task.Title })
        });
    }

    private void AddLinkActivities(TaskItem child, TaskItem parent, Guid userId, DateTimeOffset now)
    {
        var projectKey = child.Project?.Key ?? parent.Project.Key;
        var childKey = DisplayKey(projectKey, child.Number);
        var parentKey = DisplayKey(parent.Project.Key, parent.Number);
        AddParentActivity(parent.Id, TaskActivityType.ChildAdded, child.Id, childKey, userId, now);
        AddParentActivity(child.Id, TaskActivityType.ParentSet, parent.Id, parentKey, userId, now);
    }

    private void AddParentActivity(
        Guid taskId,
        TaskActivityType type,
        Guid otherTaskId,
        string otherDisplayKey,
        Guid userId,
        DateTimeOffset now)
    {
        _db.TaskActivities.Add(new TaskActivity
        {
            Id = Guid.NewGuid(),
            TaskId = taskId,
            Type = type,
            ActorUserId = userId,
            CreatedAt = now,
            Details = ActivityJson.Of(new
            {
                otherTaskId,
                otherDisplayKey,
                source = ParentTaskErrors.ManualSource
            })
        });
    }

    private static string DisplayKey(string projectKey, int number) => $"{projectKey}-{number}";
}
