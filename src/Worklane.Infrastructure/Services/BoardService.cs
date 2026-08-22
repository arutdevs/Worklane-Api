using Microsoft.EntityFrameworkCore;
using Worklane.Application.Board.Dtos;
using Worklane.Application.Board.Interfaces;
using Worklane.Application.Common.Exceptions;
using Worklane.Domain.Entities;
using Worklane.Domain.Enums;
using Worklane.Infrastructure.Identity;
using Worklane.Infrastructure.Persistence;

namespace Worklane.Infrastructure.Services;

public class BoardService : IBoardService
{
    private readonly WorklaneDbContext _db;

    public BoardService(WorklaneDbContext db)
    {
        _db = db;
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

        return columns.Select(col =>
        {
            var colTasks = tasks
                .Where(t => t.ColumnId == col.Id)
                .Select(t => ToDto(t, project.Key, assigneeMap))
                .ToList();

            return new BoardColumnDto(col.Id, col.ProjectId, col.Name, col.Order, col.IsDone, colTasks);
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
            IsDone = false
        };

        _db.BoardColumns.Add(column);
        await _db.SaveChangesAsync(ct);

        return new BoardColumnDto(column.Id, column.ProjectId, column.Name, column.Order, column.IsDone, []);
    }

    public async Task<BoardColumnDto> UpdateColumnAsync(Guid columnId, UpdateColumnRequest request, CancellationToken ct = default)
    {
        var column = await _db.BoardColumns.FirstOrDefaultAsync(c => c.Id == columnId, ct)
            ?? throw new NotFoundException(nameof(BoardColumn), columnId);

        if (request.Name is not null) column.Name = request.Name;
        if (request.Order.HasValue) column.Order = request.Order.Value;

        await _db.SaveChangesAsync(ct);
        return new BoardColumnDto(column.Id, column.ProjectId, column.Name, column.Order, column.IsDone, []);
    }

    public async Task DeleteColumnAsync(Guid columnId, Guid? targetColumnId, CancellationToken ct = default)
    {
        var column = await _db.BoardColumns
            .Include(c => c.Tasks)
            .FirstOrDefaultAsync(c => c.Id == columnId, ct)
            ?? throw new NotFoundException(nameof(BoardColumn), columnId);

        if (column.Tasks.Count > 0)
        {
            if (targetColumnId is null)
                throw new ConflictException("Column has tasks. Provide targetColumnId to move them first.");

            var target = await _db.BoardColumns.FirstOrDefaultAsync(c => c.Id == targetColumnId.Value, ct)
                ?? throw new NotFoundException(nameof(BoardColumn), targetColumnId.Value);

            var maxOrder = await _db.TaskItems
                .Where(t => t.ColumnId == target.Id)
                .MaxAsync(t => (double?)t.Order, ct) ?? 0;

            int i = 0;
            foreach (var task in column.Tasks)
            {
                task.ColumnId = targetColumnId.Value;
                task.Order = maxOrder + (++i) * 65536.0;
            }
        }

        _db.BoardColumns.Remove(column);
        await _db.SaveChangesAsync(ct);
    }

    // ── Tasks ─────────────────────────────────────────────────────────────────

    public async Task<TaskItemDto> CreateTaskAsync(Guid projectId, CreateTaskRequest request, Guid userId, CancellationToken ct = default)
    {
        var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw new NotFoundException(nameof(Project), projectId);

        // Resolve target column (default = first column by order)
        Guid columnId;
        if (request.ColumnId.HasValue)
        {
            columnId = request.ColumnId.Value;
        }
        else
        {
            var firstColId = await _db.BoardColumns
                .Where(c => c.ProjectId == projectId)
                .OrderBy(c => c.Order)
                .Select(c => c.Id)
                .FirstOrDefaultAsync(ct);

            if (firstColId == Guid.Empty)
                throw new ConflictException("Project has no columns. Create a column first.");
            columnId = firstColId;
        }

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
            CreatedById = userId,
            CreatedAt = now,
            UpdatedAt = now,
            IsArchived = false
        };

        _db.TaskItems.Add(task);
        await _db.SaveChangesAsync(ct);

        return ToDto(task, project.Key, await BuildAssigneeMapAsync([task], ct));
    }

    public async Task<TaskItemDto> GetTaskAsync(Guid taskId, CancellationToken ct = default)
    {
        var task = await _db.TaskItems
            .Include(t => t.Project)
            .FirstOrDefaultAsync(t => t.Id == taskId, ct)
            ?? throw new NotFoundException(nameof(TaskItem), taskId);

        return ToDto(task, task.Project.Key, await BuildAssigneeMapAsync([task], ct));
    }

    public async Task<TaskItemDto> UpdateTaskAsync(Guid taskId, UpdateTaskRequest request, CancellationToken ct = default)
    {
        var task = await _db.TaskItems
            .FirstOrDefaultAsync(t => t.Id == taskId, ct)
            ?? throw new NotFoundException(nameof(TaskItem), taskId);

        if (request.Title is not null) task.Title = request.Title;
        if (request.Description is not null) task.Description = request.Description;
        if (request.AssigneeId is not null) task.AssigneeId = request.AssigneeId;
        if (request.Priority.HasValue) task.Priority = request.Priority.Value;
        if (request.DueDate is not null) task.DueDate = request.DueDate;
        if (request.ColumnId.HasValue) task.ColumnId = request.ColumnId.Value;

        task.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        return await GetTaskAsync(taskId, ct);
    }

    public async Task<TaskItemDto> MoveTaskAsync(Guid taskId, MoveTaskRequest request, CancellationToken ct = default)
    {
        var task = await _db.TaskItems
            .FirstOrDefaultAsync(t => t.Id == taskId, ct)
            ?? throw new NotFoundException(nameof(TaskItem), taskId);

        var columnExists = await _db.BoardColumns.AnyAsync(c => c.Id == request.ToColumnId, ct);
        if (!columnExists)
            throw new NotFoundException(nameof(BoardColumn), request.ToColumnId);

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

        task.ColumnId = request.ToColumnId;
        task.Order = newOrder;
        task.UpdatedAt = DateTimeOffset.UtcNow;

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
        return await GetTaskAsync(taskId, ct);
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

        return tasks.Select(t => ToDto(t, project.Key, assigneeMap)).ToList();
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

    private static TaskItemDto ToDto(
        TaskItem t,
        string projectKey,
        Dictionary<Guid, (string DisplayName, string? AvatarUrl)> assigneeMap)
    {
        (string? name, string? avatar) = t.AssigneeId.HasValue && assigneeMap.TryGetValue(t.AssigneeId.Value, out var a)
            ? a
            : (null, null);

        return new TaskItemDto(
            t.Id, t.ProjectId, t.ColumnId,
            t.Number, $"{projectKey}-{t.Number}",
            t.Title, t.Description,
            t.AssigneeId, name, avatar,
            t.Priority, t.DueDate, t.Order,
            t.CreatedById, t.CreatedAt, t.UpdatedAt, t.IsArchived);
    }
}
