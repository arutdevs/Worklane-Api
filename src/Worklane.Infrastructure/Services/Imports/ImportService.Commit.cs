using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using Worklane.Application.Board;
using Worklane.Application.Common.Exceptions;
using Worklane.Application.Imports;
using Worklane.Application.Imports.Dtos;
using Worklane.Domain.Entities;
using Worklane.Domain.Enums;
using Worklane.Infrastructure.Persistence;

namespace Worklane.Infrastructure.Services.Imports;

public partial class ImportService
{
    public async Task<ImportCommitDto> CommitAsync(
        Guid projectId,
        Guid importId,
        Guid userId,
        CommitImportRequest request,
        CancellationToken cancellationToken)
    {
        var skipInvalid = request.SkipInvalidRows!.Value;
        var previousTimeout = _db.Database.GetCommandTimeout();
        _db.Database.SetCommandTimeout(_settings.CommitCommandTimeoutSeconds);
        try
        {
            return await DbRetry.ExecuteAsync(_db, async token =>
            {
                await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
                try
                {
                    var result = await CommitLockedAsync(projectId, importId, userId, skipInvalid, token);
                    await transaction.CommitAsync(token);
                    await DeleteCommittedFileAsync(importId, token);
                    await BroadcastCommitAsync(projectId, result, token);
                    return result.Dto;
                }
                catch (Exception ex) when (UniqueConstraintConflicts.Translate(ex) is { } conflict)
                {
                    throw conflict;
                }
                catch (Exception ex) when (IsCommitTimeout(ex, token))
                {
                    throw new ConflictException(ImportErrors.Timeout, ImportErrors.TimeoutDetail);
                }
            }, cancellationToken);
        }
        finally
        {
            _db.Database.SetCommandTimeout(previousTimeout);
        }
    }

    public async Task<ImportUndoDto> UndoAsync(Guid projectId, Guid importId, CancellationToken cancellationToken)
    {
        return await DbRetry.ExecuteAsync(_db, async token =>
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
            var result = await UndoLockedAsync(projectId, importId, token);
            await transaction.CommitAsync(token);
            await _notifier.ImportUndoneAsync(projectId, result.Event, token);
            return result.Dto;
        }, cancellationToken);
    }

    private async Task<CommitOutcome> CommitLockedAsync(
        Guid projectId,
        Guid importId,
        Guid userId,
        bool skipInvalid,
        CancellationToken cancellationToken)
    {
        await LockBatchAsync(projectId, importId, cancellationToken);
        var batch = await _db.ImportBatches.SingleAsync(b => b.Id == importId && b.ProjectId == projectId, cancellationToken);
        if (batch.Status is not ImportBatchStatus.Committed and not ImportBatchStatus.Undone
            && (batch.Status == ImportBatchStatus.Expired || batch.ExpiresAt <= DateTimeOffset.UtcNow))
            throw new ConflictException(ImportErrors.Expired, ImportErrors.ExpiredDetail);
        switch (batch.Status)
        {
            case ImportBatchStatus.Uploaded:
                throw new ConflictException(ImportErrors.MappingRequired, ImportErrors.MappingRequiredDetail);
            case ImportBatchStatus.Committed:
                throw new ConflictException(ImportErrors.AlreadyCommitted, ImportErrors.AlreadyCommittedDetail);
            case ImportBatchStatus.Undone:
                throw new ConflictException(ImportErrors.AlreadyUndone, ImportErrors.AlreadyUndoneDetail);
            case ImportBatchStatus.Expired:
                throw new ConflictException(ImportErrors.Expired, ImportErrors.ExpiredDetail);
        }

        if (batch.FallbackColumnId is not Guid fallback)
            throw new ConflictException(ImportErrors.ColumnMissing, ImportErrors.ColumnMissingDetail);

        var project = await _db.Projects.SingleOrDefaultAsync(p => p.Id == projectId, cancellationToken)
            ?? throw new NotFoundException(ImportErrors.NotFoundDetail);
        var columns = await _db.BoardColumns.Where(c => c.ProjectId == projectId).ToListAsync(cancellationToken);
        if (columns.Count == 0)
            throw new ConflictException(ImportErrors.NoColumnsDetail);
        if (columns.All(c => c.Id != fallback))
            throw new ConflictException(ImportErrors.ColumnMissing, ImportErrors.ColumnMissingDetail);

        var table = await ReadTableAsync(batch, cancellationToken);
        var evaluated = await EvaluateAsync(batch, table, fallback, cancellationToken);
        var blocking = evaluated.Rows.Count(row => row.ErrorCode is not null);
        if (blocking > 0 && !skipInvalid)
        {
            throw new ConflictException(
                ImportErrors.HasErrors,
                ImportErrors.HasErrorsDetail(blocking),
                ErrorParams.Of(("invalidCount", blocking)));
        }

        var now = DateTimeOffset.UtcNow;
        var orders = new Dictionary<Guid, double>();
        var created = new List<CreatedCard>();
        foreach (var row in evaluated.Rows.Where(row => row.WillCreate))
        {
            var column = columns.Single(c => c.Id == row.ColumnId);
            var number = await NextTaskNumberAsync(projectId, cancellationToken);
            var order = await NextOrderAsync(column.Id, orders, cancellationToken);
            var task = new TaskItem
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                ColumnId = column.Id,
                Number = number,
                Title = row.Title ?? "",
                Description = row.Description,
                AssigneeId = row.AssigneeId,
                Priority = row.Priority,
                DueDate = row.DueDate,
                Order = order,
                EstimateDays = row.EstimateDays,
                StartedAt = column.IsInProgress ? now : null,
                CompletedAt = column.IsDone ? now : null,
                CreatedById = userId,
                CreatedAt = now,
                UpdatedAt = now,
                IsArchived = false,
                ArchivedAt = null,
                ParentTaskId = null,
                ImportSource = batch.Source,
                ExternalId = row.ExternalId,
                ImportBatchId = batch.Id
            };
            _db.TaskItems.Add(task);
            _db.TaskActivities.Add(new TaskActivity
            {
                Id = Guid.NewGuid(),
                TaskId = task.Id,
                Type = TaskActivityType.Created,
                ToColumnId = column.Id,
                ActorUserId = userId,
                CreatedAt = now,
                Details = ActivityJson.Of(new
                {
                    columnId = column.Id,
                    columnName = column.Name,
                    title = task.Title,
                    source = "import",
                    importBatchId = batch.Id,
                    externalId = task.ExternalId
                })
            });
            created.Add(new CreatedCard(row, task));
        }

        // Pass 1 has to be in the database before the parent locks can see the new cards.
        // A later failure still rolls this transaction back.
        if (created.Count > 0)
            await _db.SaveChangesAsync(cancellationToken);

        var linkedParents = await LinkParentsAsync(batch, project.Key, userId, now, created, skipInvalid, cancellationToken);
        var taskIds = created.ToDictionary(card => card.Row.RowNumber, card => card.Task.Id);
        var displayKeys = created.ToDictionary(card => card.Row.RowNumber, card => project.Key + "-" + card.Task.Number);

        var dtoRows = new List<ImportRowDto>(evaluated.Rows.Count);
        var createdCount = 0;
        var skippedExisting = 0;
        var skippedArchived = 0;
        var skippedInvalid = 0;
        var skippedSource = 0;
        var linked = 0;
        var unlinked = 0;
        foreach (var row in evaluated.Rows)
        {
            var status = FinalStatus(row, skipInvalid);
            Guid? taskId = row.ExistingTaskId;
            string? displayKey = row.DisplayKey;
            if (taskIds.TryGetValue(row.RowNumber, out var createdId))
            {
                taskId = createdId;
                displayKey = displayKeys[row.RowNumber];
            }

            switch (status)
            {
                case ImportRowStatus.Created: createdCount++; break;
                case ImportRowStatus.SkippedExisting: skippedExisting++; break;
                case ImportRowStatus.SkippedArchived: skippedArchived++; break;
                case ImportRowStatus.SkippedInvalid: skippedInvalid++; break;
                case ImportRowStatus.SkippedSourceArchived: skippedSource++; break;
            }

            if (status == ImportRowStatus.Created && row.ParentRequested)
                linked++;
            if (status == ImportRowStatus.Created && row.HadParent && !row.ParentRequested)
                unlinked++;

            dtoRows.Add(ToRow(row, status, taskId, displayKey));
        }

        await ReplaceRowsAsync(batch, evaluated.Rows, taskIds, skipInvalid, cancellationToken);
        var storedRows = _db.ImportBatchRows.Local.Where(r => r.ImportBatchId == batch.Id).ToList();
        foreach (var stored in storedRows)
            NullSensitive(stored);

        batch.SampleJson = null;
        batch.Status = ImportBatchStatus.Committed;
        batch.WarningsJson = ImportJsonStore.Of(WarningsFor(
            ImportJsonStore.Mapping(batch.MappingJson)!,
            table,
            evaluated.ParentAssumption));
        await _db.SaveChangesAsync(cancellationToken);

        return new CommitOutcome(
            new ImportCommitDto
            {
                BatchId = batch.Id,
                Status = ImportBatchStatus.Committed,
                CreatedCount = createdCount,
                SkippedExistingCount = skippedExisting,
                SkippedArchivedCount = skippedArchived,
                SkippedInvalidCount = skippedInvalid,
                SkippedSourceArchivedCount = skippedSource,
                LinkedCount = linked,
                UnlinkedCount = unlinked,
                Rows = dtoRows
            },
            created.Select(card => card.Task.Id).ToList(),
            linkedParents);

        // File bytes stay until the transaction commits. ImportRetentionService deletes a
        // committed batch whose storage key is still set if this process stops before that.
    }

    private async Task<List<Guid>> LinkParentsAsync(
        ImportBatch batch,
        string projectKey,
        Guid userId,
        DateTimeOffset now,
        List<CreatedCard> created,
        bool skipInvalid,
        CancellationToken cancellationToken)
    {
        var wanted = created.Where(card => card.Row.ParentRequested && !string.IsNullOrEmpty(card.Row.ParentExternalId)).ToList();
        if (wanted.Count == 0)
            return [];

        var workspaceId = RequireWorkspace();
        var byExternal = created
            .Where(card => !string.IsNullOrEmpty(card.Row.ExternalId))
            .ToDictionary(card => card.Row.ExternalId!, card => card.Task, StringComparer.Ordinal);
        var parentIds = new List<Guid>();
        foreach (var card in wanted)
        {
            if (byExternal.TryGetValue(card.Row.ParentExternalId!, out var parent))
            {
                parentIds.Add(parent.Id);
                continue;
            }

            // Same project and workspace only. An external id that exists in another tenant is not a parent.
            var existingId = await _db.TaskItems
                .IgnoreQueryFilters()
                .Where(t => t.ProjectId == batch.ProjectId
                    && t.Project.WorkspaceId == workspaceId
                    && t.ImportSource == batch.Source
                    && t.ExternalId == card.Row.ParentExternalId
                    && !t.IsArchived)
                .Select(t => (Guid?)t.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (existingId is Guid id)
                parentIds.Add(id);
        }

        var lockIds = wanted.Select(card => card.Task.Id).Concat(parentIds).Distinct().OrderBy(id => id).ToArray();
        if (lockIds.Length > 0)
            await LockTasksAsync(workspaceId, lockIds, cancellationToken);

        var locked = await _db.TaskItems
            .IgnoreQueryFilters()
            .Where(t => lockIds.Contains(t.Id) && t.Project.WorkspaceId == workspaceId && t.ProjectId == batch.ProjectId)
            .ToListAsync(cancellationToken);
        var byId = locked.ToDictionary(t => t.Id);
        var added = new Dictionary<Guid, int>();
        var linkedParents = new List<Guid>();
        foreach (var card in wanted)
        {
            TaskItem? parent = null;
            if (byExternal.TryGetValue(card.Row.ParentExternalId!, out var createdParent))
                parent = byId.GetValueOrDefault(createdParent.Id);
            else
            {
                parent = locked.FirstOrDefault(t =>
                    t.ImportSource == batch.Source
                    && t.ExternalId == card.Row.ParentExternalId
                    && !t.IsArchived);
            }

            var child = byId[card.Task.Id];
            string? failure = null;
            if (parent is null || parent.IsArchived || parent.ProjectId != batch.ProjectId)
                failure = ImportErrors.ParentUnresolved;
            else if (parent.ParentTaskId is not null)
                failure = ParentTaskErrors.ParentDepthExceeded;
            else
            {
                var active = await _db.TaskItems.CountAsync(t => t.ParentTaskId == parent.Id, cancellationToken);
                if (active + added.GetValueOrDefault(parent.Id) >= _maxChildren)
                    failure = ParentTaskErrors.ParentChildLimit;
            }

            if (failure is not null)
            {
                if (!skipInvalid)
                {
                    throw new ConflictException(
                        ImportErrors.HasErrors,
                        ImportErrors.HasErrorsDetail(1),
                        ErrorParams.Of(("invalidCount", 1)));
                }

                card.Row.WarningCode = failure;
                card.Row.ParentRequested = false;
                if (failure == ParentTaskErrors.ParentChildLimit)
                    card.Row.MaxChildren = _maxChildren;
                continue;
            }

            child.ParentTaskId = parent!.Id;
            child.UpdatedAt = child.CreatedAt;
            if (parent.ImportBatchId == batch.Id)
                parent.UpdatedAt = parent.CreatedAt;
            var childKey = projectKey + "-" + child.Number;
            var parentKey = projectKey + "-" + parent.Number;
            AddLink(parent.Id, TaskActivityType.ChildAdded, child.Id, childKey, userId, now, batch.Id);
            AddLink(child.Id, TaskActivityType.ParentSet, parent.Id, parentKey, userId, now, batch.Id);
            added[parent.Id] = added.GetValueOrDefault(parent.Id) + 1;
            linkedParents.Add(parent.Id);
        }

        return linkedParents.Distinct().ToList();
    }

    private void AddLink(
        Guid taskId,
        TaskActivityType type,
        Guid otherTaskId,
        string otherDisplayKey,
        Guid userId,
        DateTimeOffset now,
        Guid batchId)
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
                source = "import",
                importBatchId = batchId
            })
        });
    }

    private async Task DeleteCommittedFileAsync(Guid importId, CancellationToken cancellationToken)
    {
        var batch = await _db.ImportBatches.SingleOrDefaultAsync(b => b.Id == importId, cancellationToken);
        if (batch?.StorageKey is not { } key)
            return;
        await _files.DeleteAsync(key, cancellationToken);
        batch.StorageKey = null;
        batch.FileDeletedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task BroadcastCommitAsync(Guid projectId, CommitOutcome outcome, CancellationToken cancellationToken)
    {
        var createdCount = outcome.Dto.CreatedCount;
        var skipped = outcome.Dto.SkippedExistingCount
            + outcome.Dto.SkippedArchivedCount
            + outcome.Dto.SkippedInvalidCount
            + outcome.Dto.SkippedSourceArchivedCount;
        var refetch = createdCount > _settings.RealtimeFanoutLimit;
        if (!refetch)
        {
            foreach (var taskId in outcome.CreatedTaskIds)
            {
                var dto = await _board.GetTaskAsync(taskId, cancellationToken);
                await _notifier.TaskCreatedAsync(projectId, dto, cancellationToken);
            }

            foreach (var parentId in outcome.LinkedParentIds)
            {
                var dto = await _board.GetTaskAsync(parentId, cancellationToken);
                await _notifier.TaskUpdatedAsync(projectId, dto, cancellationToken);
            }
        }

        await _notifier.ImportCompletedAsync(projectId, new ImportCompletedDto
        {
            ProjectId = projectId,
            ImportId = outcome.Dto.BatchId,
            CreatedCount = createdCount,
            SkippedCount = skipped,
            Refetch = refetch
        }, cancellationToken);
    }

    private async Task<UndoOutcome> UndoLockedAsync(Guid projectId, Guid importId, CancellationToken cancellationToken)
    {
        await LockBatchAsync(projectId, importId, cancellationToken);
        var batch = await _db.ImportBatches.SingleAsync(b => b.Id == importId && b.ProjectId == projectId, cancellationToken);
        switch (batch.Status)
        {
            case ImportBatchStatus.Uploaded or ImportBatchStatus.Mapped or ImportBatchStatus.Previewed:
                throw new ConflictException(ImportErrors.NotCommitted, ImportErrors.NotCommittedDetail);
            case ImportBatchStatus.Undone:
                throw new ConflictException(ImportErrors.AlreadyUndone, ImportErrors.AlreadyUndoneDetail);
            case ImportBatchStatus.Expired:
                throw new ConflictException(ImportErrors.Expired, ImportErrors.ExpiredDetail);
            case ImportBatchStatus.Committed:
                break;
            default:
                throw new ConflictException(ImportErrors.NotCommitted, ImportErrors.NotCommittedDetail);
        }

        var workspaceId = RequireWorkspace();
        var project = await _db.Projects.SingleAsync(p => p.Id == projectId, cancellationToken);
        // Undo must see archived cards from this batch. The task filter would hide them.
        var tasks = await _db.TaskItems
            .IgnoreQueryFilters()
            .Where(t => t.ImportBatchId == batch.Id && t.ProjectId == projectId && t.Project.WorkspaceId == workspaceId)
            .ToListAsync(cancellationToken);
        var ids = tasks.Select(t => t.Id).ToArray();
        if (ids.Length > 0)
            await LockTasksAsync(workspaceId, ids.OrderBy(id => id).ToArray(), cancellationToken);

        var comments = await _db.TaskComments
            .IgnoreQueryFilters()
            .Where(c => ids.Contains(c.TaskId) && c.Task.Project.WorkspaceId == workspaceId)
            .Select(c => c.TaskId)
            .ToListAsync(cancellationToken);
        var attachments = await _db.TaskAttachments
            .IgnoreQueryFilters()
            .Where(a => ids.Contains(a.TaskId) && a.Task.Project.WorkspaceId == workspaceId)
            .Select(a => a.TaskId)
            .ToListAsync(cancellationToken);
        var activities = await _db.TaskActivities
            .IgnoreQueryFilters()
            .Where(a => ids.Contains(a.TaskId) && a.Task.Project.WorkspaceId == workspaceId)
            .Select(a => new { a.TaskId, a.Type, a.Details })
            .ToListAsync(cancellationToken);
        var activityByTask = activities
            .GroupBy(a => a.TaskId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(a => new ActivitySnap { TaskId = a.TaskId, Type = a.Type, Details = a.Details }).ToList());
        var commentIds = comments.ToHashSet();
        var attachmentIds = attachments.ToHashSet();

        var reasons = new Dictionary<Guid, ImportUndoReason>();
        foreach (var task in tasks)
        {
            if (task.IsArchived)
                reasons[task.Id] = ImportUndoReason.AlreadyArchived;
            else if (!IsPristine(task, batch.Id, commentIds, attachmentIds, activityByTask))
                reasons[task.Id] = ImportUndoReason.EditedAfterImport;
        }

        var archive = tasks.Where(t => !reasons.ContainsKey(t.Id)).Select(t => t.Id).ToHashSet();
        var children = await _db.TaskItems
            .IgnoreQueryFilters()
            .Where(t => t.ParentTaskId != null
                && ids.Contains(t.ParentTaskId.Value)
                && t.Project.WorkspaceId == workspaceId
                && !t.IsArchived)
            .Select(t => new { t.Id, ParentId = t.ParentTaskId!.Value })
            .ToListAsync(cancellationToken);
        var childrenByParent = children.GroupBy(c => c.ParentId).ToDictionary(g => g.Key, g => g.Select(c => c.Id).ToList());
        bool changed;
        do
        {
            changed = false;
            foreach (var id in archive.ToList())
            {
                if (!childrenByParent.TryGetValue(id, out var kids))
                    continue;
                if (kids.Any(childId => !archive.Contains(childId)))
                {
                    archive.Remove(id);
                    reasons[id] = ImportUndoReason.HasActiveChildren;
                    changed = true;
                }
            }
        }
        while (changed);

        var archivedAt = DateTimeOffset.UtcNow;
        foreach (var task in tasks.Where(t => archive.Contains(t.Id)))
        {
            task.IsArchived = true;
            task.ArchivedAt = archivedAt;
        }

        await ClearSensitiveAsync(batch, cancellationToken);
        batch.Status = ImportBatchStatus.Undone;
        await _db.SaveChangesAsync(cancellationToken);

        var kept = tasks
            .Where(t => reasons.ContainsKey(t.Id))
            .Select(t => new ImportUndoKeptDto
            {
                TaskId = t.Id,
                DisplayKey = project.Key + "-" + t.Number,
                Reason = reasons[t.Id]
            })
            .ToList();
        var archivedIds = tasks.Where(t => archive.Contains(t.Id)).Select(t => t.Id).ToList();
        var refetch = archivedIds.Count > _settings.RealtimeFanoutLimit;
        return new UndoOutcome(
            new ImportUndoDto
            {
                BatchId = batch.Id,
                Status = ImportBatchStatus.Undone,
                ArchivedCount = archivedIds.Count,
                Kept = kept
            },
            new ImportUndoneDto
            {
                ProjectId = projectId,
                ImportId = batch.Id,
                ArchivedTaskIds = refetch ? [] : archivedIds,
                Refetch = refetch
            });
    }

    private static bool IsPristine(
        TaskItem task,
        Guid batchId,
        HashSet<Guid> comments,
        HashSet<Guid> attachments,
        Dictionary<Guid, List<ActivitySnap>> activities)
    {
        if (task.IsArchived || comments.Contains(task.Id) || attachments.Contains(task.Id))
            return false;
        if (Math.Abs((task.UpdatedAt - task.CreatedAt).TotalMilliseconds) > 1)
            return false;
        if (!activities.TryGetValue(task.Id, out var rows) || rows.Count == 0)
            return false;
        if (rows.Count(row => row.Type == TaskActivityType.Created) != 1)
            return false;
        if (rows.Count(row => row.Type == TaskActivityType.ParentSet) > 1)
            return false;
        return rows.All(row =>
            row.Type is TaskActivityType.Created or TaskActivityType.ParentSet or TaskActivityType.ChildAdded
            && IsImportActivity(row.Details, batchId));
    }

    private async Task LockTasksAsync(Guid workspaceId, Guid[] ids, CancellationToken cancellationToken)
    {
        if (ids.Length == 0)
            return;
        await LockTaskIdsAsync(workspaceId, ids, cancellationToken);
    }

    private async Task<List<Guid>> LockTaskIdsAsync(Guid workspaceId, Guid[] ids, CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)_db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT t."Id"
            FROM "TaskItems" AS t
            INNER JOIN "Projects" AS p ON p."Id" = t."ProjectId"
            WHERE p."WorkspaceId" = @workspaceId
              AND t."Id" = ANY(@ids)
            ORDER BY t."Id"
            FOR UPDATE OF t
            """,
            connection);
        if (_db.Database.CurrentTransaction?.GetDbTransaction() is not NpgsqlTransaction transaction)
            throw new InvalidOperationException("Import lock requires an open transaction.");
        command.Transaction = transaction;
        command.Parameters.Add(new NpgsqlParameter("workspaceId", NpgsqlDbType.Uuid) { Value = workspaceId });
        command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids });
        var locked = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            locked.Add(reader.GetGuid(0));
        return locked;
    }

    private async Task<int> NextTaskNumberAsync(Guid projectId, CancellationToken cancellationToken)
    {
        // INSERT ... RETURNING is non-composable. Materialise it. Do not add another LINQ operator.
        var numbers = await _db.Database.SqlQuery<int>(
            $"""
            INSERT INTO "ProjectTaskCounters" ("ProjectId", "LastNumber")
            VALUES ({projectId}, 1)
            ON CONFLICT ("ProjectId") DO UPDATE
            SET "LastNumber" = "ProjectTaskCounters"."LastNumber" + 1
            RETURNING "LastNumber" AS "Value"
            """).ToListAsync(cancellationToken);
        return numbers[0];
    }

    private async Task<double> NextOrderAsync(Guid columnId, Dictionary<Guid, double> orders, CancellationToken cancellationToken)
    {
        if (!orders.TryGetValue(columnId, out var max))
            max = await _db.TaskItems.Where(t => t.ColumnId == columnId).MaxAsync(t => (double?)t.Order, cancellationToken) ?? 0;
        var next = max + 65536d;
        orders[columnId] = next;
        return next;
    }

    private static bool IsCommitTimeout(Exception exception, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || exception is AppException)
            return false;
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is TimeoutException)
                return true;
            if (current is NpgsqlException npgsql && npgsql.SqlState == PostgresErrorCodes.QueryCanceled)
                return true;
        }

        return exception is OperationCanceledException;
    }

    private sealed record CreatedCard(EvaluatedRow Row, TaskItem Task);

    private sealed record CommitOutcome(ImportCommitDto Dto, IReadOnlyList<Guid> CreatedTaskIds, IReadOnlyList<Guid> LinkedParentIds);

    private sealed record UndoOutcome(ImportUndoDto Dto, ImportUndoneDto Event);

    private sealed class ActivitySnap
    {
        public Guid TaskId { get; init; }
        public TaskActivityType Type { get; init; }
        public string? Details { get; init; }
    }
}
