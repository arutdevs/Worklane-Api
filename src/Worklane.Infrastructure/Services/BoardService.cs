using Microsoft.EntityFrameworkCore;
using Npgsql;
using Worklane.Application.Board.Dtos;
using Worklane.Application.Board.Interfaces;
using Worklane.Application.Common.Exceptions;
using Worklane.Application.Common.Interfaces;
using Worklane.Domain.Common;
using Worklane.Domain.Entities;
using Worklane.Domain.Enums;
using Worklane.Infrastructure.Identity;
using Worklane.Infrastructure.Persistence;

namespace Worklane.Infrastructure.Services;

public class BoardService : IBoardService
{
    private readonly WorklaneDbContext _db;
    private readonly IBoardNotifier _notifier;
    private readonly ICurrentTenantService _tenant;

    public BoardService(WorklaneDbContext db, IBoardNotifier notifier, ICurrentTenantService tenant)
    {
        _db = db;
        _notifier = notifier;
        _tenant = tenant;
    }

    // ── Columns ──────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<BoardColumnDto>> GetColumnsAsync(Guid projectId, CancellationToken ct = default)
    {
        var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw new NotFoundException(nameof(Project), projectId);

        var columns = await _db.BoardColumns
            .Where(c => c.ProjectId == projectId)
            .OrderBy(c => c.Order)
            .ToListAsync(ct);

        var tasks = await _db.TaskItems
            .Where(t => t.ProjectId == projectId)
            .OrderBy(t => t.Order)
            .ToListAsync(ct);

        var assigneeMap = await BuildAssigneeMapAsync(tasks, ct);
        var cardCounts = await LoadCardCountsAsync(tasks.Select(t => t.Id), ct);
        var archivedCounts = await LoadArchivedTaskCountsAsync(columns.Select(c => c.Id), ct);

        return columns.Select(col =>
        {
            var colTasks = tasks.Where(t => t.ColumnId == col.Id).ToList();
            var over = IsOverWip(col.WipLimit, colTasks.Count);
            return new BoardColumnDto(
                col.Id, col.ProjectId, col.Name, col.Order, col.IsDone, col.IsInProgress,
                colTasks.Select(t => ToDto(t, project.Key, assigneeMap, over, cardCounts)).ToList(),
                col.WipLimit, colTasks.Count, over, archivedCounts.GetValueOrDefault(col.Id));
        }).ToList();
    }

    public async Task<BoardColumnDto> CreateColumnAsync(Guid projectId, CreateColumnRequest request, Guid userId, CancellationToken ct = default)
    {
        var maxOrder = await _db.BoardColumns
            .Where(c => c.ProjectId == projectId)
            .MaxAsync(c => (int?)c.Order, ct) ?? 0;

        var column = new BoardColumn
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Name = request.Name,
            Order = maxOrder + 1000,
            IsDone = false,
            IsInProgress = false,
            WipLimit = request.WipLimit
        };

        _db.BoardColumns.Add(column);
        await _db.SaveChangesAsync(ct);

        return ToColumnDto(column, 0);
    }

    public async Task<BoardColumnDto> UpdateColumnAsync(Guid columnId, UpdateColumnRequest request, CancellationToken ct = default)
    {
        var column = await _db.BoardColumns.FirstOrDefaultAsync(c => c.Id == columnId, ct)
            ?? throw new NotFoundException(nameof(BoardColumn), columnId);

        if (request.Name is not null) column.Name = request.Name;
        if (request.Order.HasValue) column.Order = request.Order.Value;
        if (request.IsDone.HasValue) column.IsDone = request.IsDone.Value;
        if (request.IsInProgress.HasValue) column.IsInProgress = request.IsInProgress.Value;
        if (request.WipLimit.IsSpecified) column.WipLimit = request.WipLimit.Value;

        await _db.SaveChangesAsync(ct);
        var taskCount = await _db.TaskItems.CountAsync(t => t.ColumnId == column.Id, ct);
        var archivedCount = await CountArchivedTasksAsync(column.Id, ct);
        return ToColumnDto(column, taskCount, archivedCount);
    }

    public async Task DeleteColumnAsync(Guid columnId, Guid? targetColumnId, CancellationToken ct = default)
    {
        var column = await _db.BoardColumns
            .FirstOrDefaultAsync(c => c.Id == columnId, ct)
            ?? throw new NotFoundException(nameof(BoardColumn), columnId);

        if (_tenant.WorkspaceId is not Guid workspaceId)
            throw new ForbiddenAccessException();

        var cards = await _db.TaskItems
            .IgnoreQueryFilters()
            .Where(t => t.ColumnId == columnId && t.Project.WorkspaceId == workspaceId)
            .OrderBy(t => t.Order)
            .ToListAsync(ct);

        if (cards.Count == 0)
        {
            _db.BoardColumns.Remove(column);
            await SaveColumnDeleteAsync(ct);
            return;
        }

        var activeCount = cards.Count(t => !t.IsArchived);
        var archivedCount = cards.Count - activeCount;

        if (targetColumnId is null)
        {
            var otherColumns = await _db.BoardColumns
                .CountAsync(c => c.ProjectId == column.ProjectId && c.Id != column.Id, ct);

            if (otherColumns == 0 && archivedCount > 0)
            {
                var parameters = new Dictionary<string, object?>
                {
                    ["archivedTaskCount"] = archivedCount
                };
                if (activeCount > 0)
                    parameters["activeTaskCount"] = activeCount;

                throw new ConflictException(
                    "last_column_has_archived_cards",
                    $"This is the project's last column and it has {archivedCount} archived card(s). Purge them or add another column first.",
                    parameters);
            }

            if (activeCount > 0)
            {
                throw new ConflictException(
                    "column_has_active_cards",
                    $"Column has {activeCount} card(s) and {archivedCount} archived card(s). Provide targetColumnId to move them first.",
                    ErrorParams.Of(("activeTaskCount", activeCount), ("archivedTaskCount", archivedCount)));
            }

            throw new ConflictException(
                "column_has_archived_cards",
                $"Column has {archivedCount} archived card(s). Choose a column to move them to.",
                ErrorParams.Of(("activeTaskCount", activeCount), ("archivedTaskCount", archivedCount)));
        }

        if (targetColumnId.Value == columnId)
        {
            throw new BadRequestException(
                "target_column_same_as_source",
                "Target column must be different from the column being deleted.");
        }

        var target = await _db.BoardColumns
            .FirstOrDefaultAsync(c => c.Id == targetColumnId.Value && c.ProjectId == column.ProjectId, ct)
            ?? throw new NotFoundException(
                "target_column_not_found",
                "Target column was not found in this project.");

        var maxOrder = await _db.TaskItems
            .IgnoreQueryFilters()
            .Where(t => t.ColumnId == target.Id && t.Project.WorkspaceId == workspaceId)
            .MaxAsync(t => (double?)t.Order, ct) ?? 0;

        var i = 0;
        foreach (var task in cards)
        {
            task.ColumnId = target.Id;
            task.Order = maxOrder + (++i) * 65536.0;
        }

        _db.BoardColumns.Remove(column);
        await SaveColumnDeleteAsync(ct);
    }

    // ── Tasks ─────────────────────────────────────────────────────────────────

    public async Task<TaskItemDto> CreateTaskAsync(Guid projectId, CreateTaskRequest request, Guid userId, CancellationToken ct = default)
    {
        var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw new NotFoundException(nameof(Project), projectId);

        // Resolve target column (default = first column by order). The whole column is
        // needed, not just its id — its IsInProgress/IsDone flags decide whether the task
        // starts the clock the moment it is created.
        if (request.AssigneeId is { } createAssigneeId)
            await EnsureAssigneeIsMemberAsync(projectId, createAssigneeId, ct);

        var column = request.ColumnId.HasValue
            ? await RequireColumnInProjectAsync(projectId, request.ColumnId.Value, ct)
            : await _db.BoardColumns
                .Where(c => c.ProjectId == projectId)
                .OrderBy(c => c.Order)
                .FirstOrDefaultAsync(ct)
                ?? throw new ConflictException("Project has no columns. Create a column first.");

        var columnId = column.Id;

        // Atomic per-project counter via PostgreSQL upsert.
        // The INSERT ... RETURNING is non-composable, so it must be materialised without
        // any LINQ operator on top of it (FirstAsync would compose a LIMIT over it and
        // throw). SqlQuery<T> shapes a scalar from a column named "Value".
        var numbers = await _db.Database.SqlQuery<int>(
            $"""
            INSERT INTO "ProjectTaskCounters" ("ProjectId", "LastNumber")
            VALUES ({projectId}, 1)
            ON CONFLICT ("ProjectId") DO UPDATE
            SET "LastNumber" = "ProjectTaskCounters"."LastNumber" + 1
            RETURNING "LastNumber" AS "Value"
            """).ToListAsync(ct);
        var number = numbers[0];

        var maxOrder = await _db.TaskItems
            .Where(t => t.ColumnId == columnId)
            .MaxAsync(t => (double?)t.Order, ct) ?? 0;

        var now = DateTimeOffset.UtcNow;
        var task = new TaskItem
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            ColumnId = columnId,
            Number = number,
            Title = request.Title,
            Description = request.Description,
            AssigneeId = request.AssigneeId,
            Priority = request.Priority,
            DueDate = request.DueDate,
            Order = maxOrder + 65536.0,
            EstimateDays = request.EstimateDays,
            StartedAt = column.IsInProgress ? now : null,
            CompletedAt = column.IsDone ? now : null,
            CreatedById = userId,
            CreatedAt = now,
            UpdatedAt = now,
            IsArchived = false
        };

        _db.TaskItems.Add(task);
        _db.TaskActivities.Add(new TaskActivity
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            Type = TaskActivityType.Created,
            FromColumnId = null,
            ToColumnId = columnId,
            ActorUserId = userId,
            CreatedAt = now,
            Details = ActivityJson.Of(new { columnId, columnName = column.Name, title = task.Title })
        });
        await _db.SaveChangesAsync(ct);

        var dto = ToDto(task, project.Key, await BuildAssigneeMapAsync([task], ct), await IsColumnOverWipAsync(columnId, ct), await LoadCardCountsAsync([task.Id], ct));
        await _notifier.TaskCreatedAsync(projectId, dto, ct);
        return dto;
    }

    public async Task<TaskItemDto> GetTaskAsync(Guid taskId, CancellationToken ct = default)
    {
        var task = await _db.TaskItems
            .Include(t => t.Project)
            .FirstOrDefaultAsync(t => t.Id == taskId, ct)
            ?? throw new NotFoundException(nameof(TaskItem), taskId);

        return ToDto(task, task.Project.Key, await BuildAssigneeMapAsync([task], ct), await IsColumnOverWipAsync(task.ColumnId, ct), await LoadCardCountsAsync([task.Id], ct));
    }

    public async Task<TaskItemDto> UpdateTaskAsync(Guid taskId, UpdateTaskRequest request, Guid userId, CancellationToken ct = default)
    {
        var task = await _db.TaskItems
            .FirstOrDefaultAsync(t => t.Id == taskId, ct)
            ?? throw new NotFoundException(nameof(TaskItem), taskId);

        var changes = new List<(string Field, object? OldValue, object? NewValue)>();
        void Track(string field, object? current, object? updated)
        {
            if (!Equals(current, updated))
                changes.Add((field, current, updated));
        }

        if (request.Title.IsSpecified)
        {
            Track("title", task.Title, request.Title.Value);
            task.Title = request.Title.Value!;
        }
        if (request.Description.IsSpecified)
        {
            Track("description", task.Description, request.Description.Value);
            task.Description = request.Description.Value;
        }
        if (request.AssigneeId.IsSpecified)
        {
            if (request.AssigneeId.Value is { } assigneeId)
                await EnsureAssigneeIsMemberAsync(task.ProjectId, assigneeId, ct);
            Track("assigneeId", IdText(task.AssigneeId), IdText(request.AssigneeId.Value));
            task.AssigneeId = request.AssigneeId.Value;
        }
        if (request.Priority.IsSpecified)
        {
            var priority = request.Priority.Value!.Value;
            Track("priority", task.Priority.ToString(), priority.ToString());
            task.Priority = priority;
        }
        if (request.DueDate.IsSpecified)
        {
            Track("dueDate", DateText(task.DueDate), DateText(request.DueDate.Value));
            task.DueDate = request.DueDate.Value;
        }
        if (request.ColumnId.IsSpecified)
        {
            var columnId = request.ColumnId.Value!.Value;
            await RequireColumnInProjectAsync(task.ProjectId, columnId, ct);
            Track("columnId", IdText(task.ColumnId), IdText(columnId));
            task.ColumnId = columnId;
        }
        if (request.EstimateDays.IsSpecified)
        {
            Track("estimateDays", task.EstimateDays, request.EstimateDays.Value);
            task.EstimateDays = request.EstimateDays.Value;
        }

        var now = DateTimeOffset.UtcNow;
        task.UpdatedAt = now;
        for (var i = 0; i < changes.Count; i++)
        {
            var (field, oldValue, newValue) = changes[i];
            _db.TaskActivities.Add(new TaskActivity
            {
                Id = Guid.NewGuid(),
                TaskId = task.Id,
                Type = TaskActivityType.Updated,
                ActorUserId = userId,
                CreatedAt = now.AddTicks(i),
                Details = ActivityJson.Of(new { field, oldValue, newValue })
            });
        }

        await _db.SaveChangesAsync(ct);

        var dto = await GetTaskAsync(taskId, ct);
        await _notifier.TaskUpdatedAsync(dto.ProjectId, dto, ct);
        return dto;
    }

    public async Task<TaskItemDto> MoveTaskAsync(Guid taskId, MoveTaskRequest request, Guid userId, CancellationToken ct = default)
    {
        var task = await _db.TaskItems
            .FirstOrDefaultAsync(t => t.Id == taskId, ct)
            ?? throw new NotFoundException(nameof(TaskItem), taskId);

        var toColumn = await RequireColumnInProjectAsync(task.ProjectId, request.ToColumnId, ct);

        var fromColumnId = task.ColumnId;
        var changedColumn = fromColumnId != toColumn.Id;

        // Siblings = all non-archived tasks in target column except the task being moved
        var siblings = await _db.TaskItems
            .Where(t => t.ColumnId == request.ToColumnId && t.Id != taskId)
            .OrderBy(t => t.Order)
            .ToListAsync(ct);

        int index = Math.Clamp(request.ToIndex, 0, siblings.Count);
        double newOrder = CalculateOrder(siblings, index);

        const double MinGap = 0.001;
        bool needsRebalance = index > 0 && index < siblings.Count
            && (siblings[index].Order - siblings[index - 1].Order) < MinGap * 2;

        var now = DateTimeOffset.UtcNow;

        task.ColumnId = request.ToColumnId;
        task.Order = newOrder;
        task.UpdatedAt = now;

        if (changedColumn)
        {
            // The clock starts the first time the task reaches a working column and is
            // not restarted afterwards — otherwise bouncing a card in and out of review
            // would keep resetting the elapsed time to zero.
            if (toColumn.IsInProgress && task.StartedAt is null)
                task.StartedAt = now;

            // CompletedAt always reflects the current state: set on arriving in a done
            // column, cleared when the task is pulled back out of one.
            task.CompletedAt = toColumn.IsDone ? now : null;

            var fromColumnName = await _db.BoardColumns
                .Where(c => c.Id == fromColumnId)
                .Select(c => c.Name)
                .FirstOrDefaultAsync(ct);

            _db.TaskActivities.Add(new TaskActivity
            {
                Id = Guid.NewGuid(),
                TaskId = task.Id,
                Type = TaskActivityType.Moved,
                FromColumnId = fromColumnId,
                ToColumnId = toColumn.Id,
                ActorUserId = userId,
                CreatedAt = now,
                Details = ActivityJson.Of(new
                {
                    fromColumnId,
                    fromColumnName,
                    toColumnId = toColumn.Id,
                    toColumnName = toColumn.Name
                })
            });
        }

        if (needsRebalance)
        {
            var reordered = new List<TaskItem>();
            reordered.AddRange(siblings.Take(index));
            reordered.Add(task);
            reordered.AddRange(siblings.Skip(index));
            for (int i = 0; i < reordered.Count; i++)
                reordered[i].Order = (i + 1) * 65536.0;
        }

        await _db.SaveChangesAsync(ct);
        var dto = await GetTaskAsync(taskId, ct);
        await _notifier.TaskMovedAsync(dto.ProjectId, dto, ct);
        return dto;
    }

    public async Task ArchiveTaskAsync(Guid taskId, CancellationToken ct = default)
    {
        var task = await _db.TaskItems.FirstOrDefaultAsync(t => t.Id == taskId, ct)
            ?? throw new NotFoundException(nameof(TaskItem), taskId);

        task.IsArchived = true;
        task.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<TaskItemDto>> GetTasksAsync(
        Guid projectId, Guid? assigneeId, TaskPriority? priority, string? search, CancellationToken ct = default)
    {
        var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw new NotFoundException(nameof(Project), projectId);

        var query = _db.TaskItems.Where(t => t.ProjectId == projectId);

        if (assigneeId.HasValue)
            query = query.Where(t => t.AssigneeId == assigneeId);
        if (priority.HasValue)
            query = query.Where(t => t.Priority == priority);
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(t => EF.Functions.ILike(t.Title, $"%{search}%"));

        var tasks = await query.OrderBy(t => t.Order).ToListAsync(ct);
        var assigneeMap = await BuildAssigneeMapAsync(tasks, ct);
        var wip = await LoadWipAsync(tasks.Select(t => t.ColumnId), ct);
        var cardCounts = await LoadCardCountsAsync(tasks.Select(t => t.Id), ct);

        return tasks.Select(t => ToDto(t, project.Key, assigneeMap, Over(wip, t.ColumnId), cardCounts)).ToList();
    }

    public async Task<IReadOnlyList<MyTaskDto>> GetMyTasksAsync(Guid workspaceId, Guid userId, CancellationToken ct = default)
    {
        var tasks = await _db.TaskItems
            .Include(t => t.Project)
            .Include(t => t.Column)
            .Where(t => t.AssigneeId == userId && t.Project.WorkspaceId == workspaceId)
            // Overdue and undated work first, then soonest due date; done columns sink
            // to the bottom regardless, since finished work is not what "my tasks" is for.
            .OrderBy(t => t.Column.IsDone)
            .ThenBy(t => t.DueDate == null)
            .ThenBy(t => t.DueDate)
            .ToListAsync(ct);

        var assigneeMap = await BuildAssigneeMapAsync(tasks, ct);
        var wip = await LoadWipAsync(tasks.Select(t => t.ColumnId), ct);
        var cardCounts = await LoadCardCountsAsync(tasks.Select(t => t.Id), ct);

        return tasks
            .Select(t => new MyTaskDto(
                ToDto(t, t.Project.Key, assigneeMap, Over(wip, t.ColumnId), cardCounts),
                t.Project.Key,
                t.Project.Name,
                t.Column.Name,
                t.Column.IsDone))
            .ToList();
    }

    // ── Auth-filter helpers ───────────────────────────────────────────────────

    public async Task<Guid?> GetWorkspaceIdForColumnAsync(Guid columnId, CancellationToken ct = default)
    {
        return await _db.BoardColumns
            .IgnoreQueryFilters()
            .Where(c => c.Id == columnId)
            .Select(c => (Guid?)c.Project.WorkspaceId)
            .SingleOrDefaultAsync(ct);
    }

    public async Task<Guid?> GetWorkspaceIdForTaskAsync(Guid taskId, CancellationToken ct = default)
    {
        return await _db.TaskItems
            .IgnoreQueryFilters()
            .Where(t => t.Id == taskId)
            .Select(t => (Guid?)t.Project.WorkspaceId)
            .SingleOrDefaultAsync(ct);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static double CalculateOrder(List<TaskItem> siblings, int index) => index switch
    {
        _ when siblings.Count == 0 => 65536.0,
        0 => siblings[0].Order / 2.0,
        _ when index >= siblings.Count => siblings[^1].Order + 65536.0,
        _ => (siblings[index - 1].Order + siblings[index].Order) / 2.0
    };

    private async Task<Dictionary<Guid, (string DisplayName, string? AvatarUrl)>> BuildAssigneeMapAsync(
        IEnumerable<TaskItem> tasks, CancellationToken ct)
    {
        var ids = tasks
            .Where(t => t.AssigneeId.HasValue)
            .Select(t => t.AssigneeId!.Value)
            .Distinct()
            .ToList();

        if (ids.Count == 0) return [];

        return await _db.Set<AppUser>()
            .Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.DisplayName, u.AvatarUrl })
            .ToDictionaryAsync(
                u => u.Id,
                u => (u.DisplayName, u.AvatarUrl),
                ct);
    }

    private static BoardColumnDto ToColumnDto(BoardColumn c, int taskCount, int archivedTaskCount = 0) =>
        new(c.Id, c.ProjectId, c.Name, c.Order, c.IsDone, c.IsInProgress, [],
            c.WipLimit, taskCount, IsOverWip(c.WipLimit, taskCount), archivedTaskCount);

    private async Task<IReadOnlyDictionary<Guid, int>> LoadArchivedTaskCountsAsync(
        IEnumerable<Guid> columnIds, CancellationToken ct)
    {
        if (_tenant.WorkspaceId is not Guid workspaceId)
            return new Dictionary<Guid, int>();

        var ids = columnIds.Distinct().ToList();
        if (ids.Count == 0)
            return new Dictionary<Guid, int>();

        var rows = await _db.TaskItems
            .IgnoreQueryFilters()
            .Where(t => ids.Contains(t.ColumnId) && t.IsArchived && t.Project.WorkspaceId == workspaceId)
            .GroupBy(t => t.ColumnId)
            .Select(g => new { ColumnId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.ColumnId, r => r.Count);
    }

    private async Task<int> CountArchivedTasksAsync(Guid columnId, CancellationToken ct)
    {
        var counts = await LoadArchivedTaskCountsAsync([columnId], ct);
        return counts.GetValueOrDefault(columnId);
    }

    private async Task SaveColumnDeleteAsync(CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsForeignKeyViolation(ex))
        {
            throw new ConflictException(
                "column_delete_conflict",
                "The column changed while it was being deleted. Reload the board and try again.");
        }
    }

    private static bool IsForeignKeyViolation(DbUpdateException exception)
    {
        for (var current = exception.InnerException; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres && postgres.SqlState == PostgresErrorCodes.ForeignKeyViolation)
                return true;
        }

        return false;
    }

    private readonly record struct ColumnWip(int? Limit, int Count)
    {
        public bool IsOver => IsOverWip(Limit, Count);
    }

    private static bool IsOverWip(int? wipLimit, int taskCount) =>
        wipLimit is int limit && taskCount > limit;

    private static bool Over(IReadOnlyDictionary<Guid, ColumnWip> wip, Guid columnId) =>
        wip.TryGetValue(columnId, out var snapshot) && snapshot.IsOver;

    private async Task<bool> IsColumnOverWipAsync(Guid columnId, CancellationToken ct)
    {
        var wip = await LoadWipAsync([columnId], ct);
        return Over(wip, columnId);
    }

    private async Task<Dictionary<Guid, ColumnWip>> LoadWipAsync(IEnumerable<Guid> columnIds, CancellationToken ct)
    {
        var ids = columnIds.Distinct().ToList();
        if (ids.Count == 0) return [];

        var limits = await _db.BoardColumns
            .Where(c => ids.Contains(c.Id))
            .Select(c => new { c.Id, c.WipLimit })
            .ToListAsync(ct);

        var counts = await _db.TaskItems
            .Where(t => ids.Contains(t.ColumnId))
            .GroupBy(t => t.ColumnId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        return limits.ToDictionary(
            c => c.Id,
            c => new ColumnWip(c.WipLimit, counts.GetValueOrDefault(c.Id)));
    }

    /// <summary>
    /// A column id from another project, including another workspace, is treated as missing
    /// so its name never lands in activity details.
    /// </summary>
    private async Task<BoardColumn> RequireColumnInProjectAsync(Guid projectId, Guid columnId, CancellationToken ct)
    {
        var column = await _db.BoardColumns
            .FirstOrDefaultAsync(c => c.Id == columnId && c.ProjectId == projectId, ct);
        return column ?? throw new NotFoundException(nameof(BoardColumn), columnId);
    }

    private async Task EnsureAssigneeIsMemberAsync(Guid projectId, Guid assigneeId, CancellationToken ct)
    {
        var workspaceId = await _db.Projects
            .Where(p => p.Id == projectId)
            .Select(p => (Guid?)p.WorkspaceId)
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException(nameof(Project), projectId);

        var isMember = await _db.WorkspaceMembers
            .AnyAsync(m => m.WorkspaceId == workspaceId && m.UserId == assigneeId, ct);

        if (!isMember)
            throw new BadRequestException("assignee_not_member", "Assignee must be a member of this workspace.");
    }

    private readonly record struct CardCounts(int Comments, int Attachments);

    /// <summary>
    /// Comment and attachment totals for a set of cards, in two grouped queries.
    /// Tasks with neither stay at zero via <see cref="Dictionary{TKey,TValue}.GetValueOrDefault"/>.
    /// </summary>
    private async Task<Dictionary<Guid, CardCounts>> LoadCardCountsAsync(IEnumerable<Guid> taskIds, CancellationToken ct)
    {
        var ids = taskIds.Distinct().ToList();
        if (ids.Count == 0) return [];

        var comments = await _db.TaskComments
            .Where(c => ids.Contains(c.TaskId))
            .GroupBy(c => c.TaskId)
            .Select(g => new { TaskId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TaskId, x => x.Count, ct);

        var attachments = await _db.TaskAttachments
            .Where(a => ids.Contains(a.TaskId))
            .GroupBy(a => a.TaskId)
            .Select(g => new { TaskId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TaskId, x => x.Count, ct);

        return ids.ToDictionary(
            id => id,
            id => new CardCounts(comments.GetValueOrDefault(id), attachments.GetValueOrDefault(id)));
    }

    private static string? IdText(Guid? id) => id?.ToString();

    private static string? DateText(DateTimeOffset? value) => value?.ToString("O");

    private static TaskItemDto ToDto(
        TaskItem t,
        string projectKey,
        Dictionary<Guid, (string DisplayName, string? AvatarUrl)> assigneeMap,
        bool isOverWipLimit,
        IReadOnlyDictionary<Guid, CardCounts> cardCounts)
    {
        (string? name, string? avatar) = t.AssigneeId.HasValue && assigneeMap.TryGetValue(t.AssigneeId.Value, out var a)
            ? a
            : (null, null);

        var (actualDays, isOverEstimate, daysOverEstimate) = CalculateEffort(t);
        var (isOverdue, daysOverdue) = CalculateOverdue(t);
        var counts = cardCounts.GetValueOrDefault(t.Id);

        return new TaskItemDto(
            t.Id, t.ProjectId, t.ColumnId,
            t.Number, $"{projectKey}-{t.Number}",
            t.Title, t.Description,
            t.AssigneeId, name, avatar,
            t.Priority, t.DueDate, t.Order,
            t.CreatedById, t.CreatedAt, t.UpdatedAt, t.IsArchived,
            t.EstimateDays, t.StartedAt, t.CompletedAt,
            actualDays, isOverEstimate, daysOverEstimate,
            isOverdue, daysOverdue,
            isOverWipLimit,
            counts.Comments, counts.Attachments);
    }

    /// <summary>
    /// Working days spent so far and how far past the estimate that is. A task still in
    /// progress keeps accruing; one that reached a done column is frozen at that moment.
    /// </summary>
    internal static (double? ActualDays, bool IsOverEstimate, double DaysOverEstimate) CalculateEffort(TaskItem t)
    {
        if (t.StartedAt is null) return (null, false, 0);

        double actual = t.CompletedAt is { } done
            ? WorkingDays.Between(t.StartedAt.Value, done)
            : WorkingDays.Since(t.StartedAt.Value);

        if (t.EstimateDays is not { } estimate || actual <= estimate)
            return (actual, false, 0);

        return (actual, true, Math.Round(actual - estimate, 2));
    }

    /// <summary>
    /// Whether the task missed its due date, and by how many working days. A finished task
    /// is judged on when it was finished, an unfinished one on today.
    /// </summary>
    internal static (bool IsOverdue, int DaysOverdue) CalculateOverdue(TaskItem t)
    {
        if (t.DueDate is not { } due) return (false, 0);

        var reference = t.CompletedAt ?? DateTimeOffset.UtcNow;
        if (reference <= due) return (false, 0);

        return (true, WorkingDays.WholeDaysBetween(due, reference));
    }
}
