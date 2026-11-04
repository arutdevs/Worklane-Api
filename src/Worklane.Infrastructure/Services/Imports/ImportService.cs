using System.Text.Json;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Worklane.Application.Board;
using Worklane.Application.Board.Interfaces;
using Worklane.Application.Common.Exceptions;
using Worklane.Application.Common.Interfaces;
using Worklane.Application.Common.Options;
using Worklane.Application.Imports;
using Worklane.Application.Imports.Dtos;
using Worklane.Application.Imports.Interfaces;
using Worklane.Domain.Entities;
using Worklane.Domain.Enums;
using Worklane.Domain.Imports;
using Worklane.Infrastructure.Persistence;

namespace Worklane.Infrastructure.Services.Imports;

public partial class ImportService : IImportService
{
    private readonly WorklaneDbContext _db;
    private readonly IFileStorage _files;
    private readonly ICurrentTenantService _tenant;
    private readonly ILookupNormalizer _normalizer;
    private readonly IBoardService _board;
    private readonly IBoardNotifier _notifier;
    private readonly ImportSettings _settings;
    private readonly int _maxChildren;

    public ImportService(
        WorklaneDbContext db,
        IFileStorage files,
        ICurrentTenantService tenant,
        ILookupNormalizer normalizer,
        IBoardService board,
        IBoardNotifier notifier,
        IOptions<ImportSettings> settings,
        IOptions<TaskSettings> tasks)
    {
        _db = db;
        _files = files;
        _tenant = tenant;
        _normalizer = normalizer;
        _board = board;
        _notifier = notifier;
        _settings = settings.Value;
        _maxChildren = tasks.Value.MaxChildrenPerParent;
    }

    public async Task<ImportBatchDto> UploadAsync(
        Guid projectId,
        Guid userId,
        string? preset,
        string? fileName,
        Stream content,
        CancellationToken cancellationToken)
    {
        if (!ImportWire.TryParseSource(preset, out var source))
        {
            throw new ValidationException(
            [
                new ValidationFailure("Preset", "Preset must be generic, jira, asana, or trello.") { ErrorCode = "invalid" }
            ]);
        }

        var safeName = SanitizeFileName(fileName);
        var extension = Path.GetExtension(safeName).ToLowerInvariant();
        var bytes = await ReadLimitedAsync(content, cancellationToken);
        if (bytes.Length == 0)
            throw new BadRequestException("file_empty", "A non-empty file is required.");
        if (bytes.Length > _settings.MaxBytes)
            throw new PayloadTooLargeException(_settings.MaxBytes);
        if (extension is not ".csv" and not ".xlsx" || !FileSignature.Matches(extension, bytes))
            throw new UnsupportedMediaTypeException(extension);

        ParsedTable table;
        try
        {
            table = ImportFileReader.Read(extension, bytes, ImportWire.Source(source), _settings);
        }
        catch (ImportFormatException ex)
        {
            throw ex.ToBadRequest();
        }

        var project = await _db.Projects.SingleOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is null)
            throw new NotFoundException(ImportErrors.NotFoundDetail);
        if (!await _db.BoardColumns.AnyAsync(c => c.ProjectId == projectId, cancellationToken))
            throw new ConflictException(ImportErrors.NoColumnsDetail);

        var now = DateTimeOffset.UtcNow;
        var batchId = Guid.NewGuid();
        var storageKey = $"imports/{batchId:N}/{Guid.NewGuid():N}";
        var suggested = ImportPresets.Suggest(ImportWire.Source(source), table.Headers);
        var warnings = WarningsFor(suggested, table, parent: null);
        var batch = new ImportBatch
        {
            Id = batchId,
            ProjectId = projectId,
            CreatedById = userId,
            Source = source,
            Status = ImportBatchStatus.Uploaded,
            OriginalFileName = safeName,
            StorageKey = storageKey,
            ExpiresAt = now.AddHours(_settings.RetentionHours),
            CreatedAt = now,
            HeadersJson = ImportJsonStore.Of(table.Headers),
            SampleJson = ImportJsonStore.Of(ImportTables.Sample(table.Rows)),
            SuggestedMappingJson = ImportJsonStore.Of(suggested),
            WarningsJson = ImportJsonStore.Of(warnings)
        };

        var stored = false;
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await LockProjectAsync(projectId, cancellationToken);
            var open = await _db.ImportBatches.CountAsync(
                b => b.ProjectId == projectId
                    && (b.Status == ImportBatchStatus.Uploaded
                        || b.Status == ImportBatchStatus.Mapped
                        || b.Status == ImportBatchStatus.Previewed)
                    && b.ExpiresAt > now,
                cancellationToken);
            if (open >= _settings.MaxOpenBatchesPerProject)
            {
                throw new ConflictException(
                    ImportErrors.BatchLimit,
                    ImportErrors.BatchLimitDetail(_settings.MaxOpenBatchesPerProject),
                    ErrorParams.Of(("limit", _settings.MaxOpenBatchesPerProject)));
            }

            await using (var body = new MemoryStream(bytes, writable: false))
                await _files.SaveAsync(storageKey, body, _settings.MaxBytes, cancellationToken);
            stored = true;
            _db.ImportBatches.Add(batch);
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            if (stored)
                await _files.DeleteAsync(storageKey, CancellationToken.None);
            throw;
        }

        return ToBatch(batch, summary: false);
    }

    public async Task<ImportBatchDto> UpdateMappingAsync(
        Guid projectId,
        Guid importId,
        UpdateImportMappingRequest request,
        CancellationToken cancellationToken)
    {
        var batch = await LoadAsync(projectId, importId, cancellationToken);
        await ExpireIfDueAsync(batch, cancellationToken);
        RejectClosed(batch);
        if (batch.Status == ImportBatchStatus.Uploaded
            || batch.Status == ImportBatchStatus.Mapped
            || batch.Status == ImportBatchStatus.Previewed)
        {
            var headers = ImportJsonStore.Headers(batch.HeadersJson);
            EnsureHeaders(headers, request.Assignments!);
            var fallback = request.FallbackColumnId!.Value;
            var column = await _db.BoardColumns.SingleOrDefaultAsync(
                c => c.Id == fallback && c.ProjectId == projectId,
                cancellationToken);
            if (column is null)
                throw new NotFoundException(ImportErrors.NotFoundDetail);

            var mapping = new ColumnMapping { Assignments = request.Assignments! };
            var keptSheets = ImportJsonStore.Warnings(batch.WarningsJson)
                .Where(warning => warning.Code == ImportErrors.ExtraSheets)
                .ToList();
            var warnings = ImportPresets.FieldIgnored(mapping).ToList();
            warnings.AddRange(keptSheets);

            batch.MappingJson = ImportJsonStore.Of(mapping);
            batch.FallbackColumnId = fallback;
            batch.Status = ImportBatchStatus.Mapped;
            batch.WarningsJson = ImportJsonStore.Of(warnings);
            var rows = await _db.ImportBatchRows.Where(r => r.ImportBatchId == batch.Id).ToListAsync(cancellationToken);
            _db.ImportBatchRows.RemoveRange(rows);
            await _db.SaveChangesAsync(cancellationToken);
            return ToBatch(batch, summary: false);
        }

        RejectClosed(batch);
        throw new ConflictException(ImportErrors.MappingRequired, ImportErrors.MappingRequiredDetail);
    }

    public async Task<ImportPreviewDto> PreviewAsync(Guid projectId, Guid importId, CancellationToken cancellationToken)
    {
        var batch = await LoadAsync(projectId, importId, cancellationToken);
        await ExpireIfDueAsync(batch, cancellationToken);
        if (batch.Status == ImportBatchStatus.Uploaded)
            throw new ConflictException(ImportErrors.MappingRequired, ImportErrors.MappingRequiredDetail);
        RejectClosed(batch);
        if (batch.FallbackColumnId is not Guid fallback)
            throw new ConflictException(ImportErrors.ColumnMissing, ImportErrors.ColumnMissingDetail);

        var table = await ReadTableAsync(batch, cancellationToken);
        var evaluated = await EvaluateAsync(batch, table, fallback, cancellationToken);
        await ReplaceRowsAsync(batch, evaluated.Rows, taskIds: null, skipInvalid: false, cancellationToken);
        batch.Status = ImportBatchStatus.Previewed;
        batch.WarningsJson = ImportJsonStore.Of(WarningsFor(ImportJsonStore.Mapping(batch.MappingJson)!, table, evaluated.ParentAssumption));
        await _db.SaveChangesAsync(cancellationToken);
        return new ImportPreviewDto
        {
            Batch = ToBatch(batch, summary: false),
            Rows = evaluated.Rows.Select(row => ToRow(row, row.Status, row.ExistingTaskId, row.DisplayKey)).ToList()
        };
    }

    public async Task<ImportPreviewDto> GetAsync(Guid projectId, Guid importId, CancellationToken cancellationToken)
    {
        var batch = await LoadAsync(projectId, importId, cancellationToken);
        await ExpireIfDueAsync(batch, cancellationToken);
        var rows = await _db.ImportBatchRows
            .Where(r => r.ImportBatchId == batch.Id)
            .OrderBy(r => r.RowNumber)
            .ToListAsync(cancellationToken);
        return new ImportPreviewDto
        {
            Batch = ToBatch(batch, summary: false),
            Rows = await ToStoredRowsAsync(batch, rows, cancellationToken)
        };
    }

    public async Task<IReadOnlyList<ImportBatchDto>> ListAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await _db.Projects.SingleOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is null)
            throw new NotFoundException(ImportErrors.NotFoundDetail);

        var batches = await _db.ImportBatches
            .Where(b => b.ProjectId == projectId)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync(cancellationToken);
        return batches.Select(batch => ToBatch(batch, summary: true)).ToList();
    }

    private async Task<ImportBatch> LoadAsync(Guid projectId, Guid importId, CancellationToken cancellationToken)
    {
        var batch = await _db.ImportBatches.SingleOrDefaultAsync(
            b => b.Id == importId && b.ProjectId == projectId,
            cancellationToken);
        if (batch is null)
            throw new NotFoundException(ImportErrors.NotFoundDetail);
        return batch;
    }

    private async Task ExpireIfDueAsync(ImportBatch batch, CancellationToken cancellationToken)
    {
        if (batch.Status is ImportBatchStatus.Committed or ImportBatchStatus.Undone)
            return;
        if (batch.Status != ImportBatchStatus.Expired && batch.ExpiresAt > DateTimeOffset.UtcNow)
            return;

        if (batch.Status != ImportBatchStatus.Expired)
        {
            await ClearSensitiveAsync(batch, cancellationToken);
            batch.Status = ImportBatchStatus.Expired;
            await _db.SaveChangesAsync(cancellationToken);
        }

        throw new ConflictException(ImportErrors.Expired, ImportErrors.ExpiredDetail);
    }

    private static void RejectClosed(ImportBatch batch)
    {
        switch (batch.Status)
        {
            case ImportBatchStatus.Committed:
                throw new ConflictException(ImportErrors.AlreadyCommitted, ImportErrors.AlreadyCommittedDetail);
            case ImportBatchStatus.Undone:
                throw new ConflictException(ImportErrors.AlreadyUndone, ImportErrors.AlreadyUndoneDetail);
            case ImportBatchStatus.Expired:
                throw new ConflictException(ImportErrors.Expired, ImportErrors.ExpiredDetail);
        }
    }

    private async Task ClearSensitiveAsync(ImportBatch batch, CancellationToken cancellationToken)
    {
        var rows = await _db.ImportBatchRows.Where(r => r.ImportBatchId == batch.Id).ToListAsync(cancellationToken);
        foreach (var row in rows)
            NullSensitive(row);
        batch.SampleJson = null;
        if (batch.StorageKey is { } key)
        {
            await _files.DeleteAsync(key, cancellationToken);
            batch.StorageKey = null;
            batch.FileDeletedAt = DateTimeOffset.UtcNow;
        }
    }

    private static void NullSensitive(ImportBatchRow row)
    {
        row.Title = null;
        row.Description = null;
        row.ColumnName = null;
        row.AssigneeEmail = null;
        row.PriorityText = null;
        row.DueDateText = null;
        row.EstimateText = null;
    }

    private async Task<ParsedTable> ReadTableAsync(ImportBatch batch, CancellationToken cancellationToken)
    {
        var bytes = await ReadStoredAsync(batch, cancellationToken);
        var extension = Path.GetExtension(batch.OriginalFileName).ToLowerInvariant();
        try
        {
            return ImportFileReader.Read(extension, bytes, ImportWire.Source(batch.Source), _settings);
        }
        catch (ImportFormatException ex)
        {
            throw ex.ToBadRequest();
        }
    }

    private async Task<byte[]> ReadStoredAsync(ImportBatch batch, CancellationToken cancellationToken)
    {
        if (batch.StorageKey is null)
            throw new BadRequestException(ImportErrors.Unreadable, ImportErrors.UnreadableDetail);

        await using var stream = await _files.OpenReadAsync(batch.StorageKey, cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        long total = 0;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            total += read;
            if (total > _settings.MaxBytes)
                throw new PayloadTooLargeException(_settings.MaxBytes);
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private async Task<EvaluatedImport> EvaluateAsync(
        ImportBatch batch,
        ParsedTable table,
        Guid fallbackColumnId,
        CancellationToken cancellationToken)
    {
        var workspaceId = RequireWorkspace();
        var columns = await _db.BoardColumns
            .Where(c => c.ProjectId == batch.ProjectId)
            .OrderBy(c => c.Order)
            .ToListAsync(cancellationToken);
        if (!columns.Any(c => c.Id == fallbackColumnId))
            throw new ConflictException(ImportErrors.ColumnMissing, ImportErrors.ColumnMissingDetail);

        var members = await _db.WorkspaceMembers
            .Where(m => m.WorkspaceId == workspaceId)
            .Join(_db.Users, member => member.UserId, user => user.Id, (_, user) => new { user.Id, user.NormalizedEmail })
            .ToListAsync(cancellationToken);
        var memberMap = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var member in members)
        {
            if (!string.IsNullOrEmpty(member.NormalizedEmail))
                memberMap.TryAdd(member.NormalizedEmail, member.Id);
        }

        // Archived cards stay in the unique index. The task filter hides them, so a miss
        // here would insert and then 500, or would report a new card instead of skipped_archived.
        var existingRows = await _db.TaskItems
            .IgnoreQueryFilters()
            .Where(t => t.ProjectId == batch.ProjectId
                && t.Project.WorkspaceId == workspaceId
                && t.ImportSource == batch.Source
                && t.ExternalId != null)
            .Select(t => new { t.Id, t.ExternalId, t.IsArchived, t.ParentTaskId, t.Number, t.Project.Key })
            .ToListAsync(cancellationToken);
        var childCounts = await _db.TaskItems
            .Where(t => t.ProjectId == batch.ProjectId && t.ParentTaskId != null)
            .GroupBy(t => t.ParentTaskId)
            .Select(g => new { ParentId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var counts = childCounts.Where(row => row.ParentId.HasValue).ToDictionary(row => row.ParentId!.Value, row => row.Count);
        var existing = existingRows.ToDictionary(
            row => row.ExternalId!,
            row => new ExistingExternalCard
            {
                Id = row.Id,
                ExternalId = row.ExternalId!,
                IsArchived = row.IsArchived,
                ParentTaskId = row.ParentTaskId,
                DisplayKey = row.Key + "-" + row.Number,
                ActiveChildCount = counts.GetValueOrDefault(row.Id)
            },
            StringComparer.Ordinal);

        return ImportRowEvaluator.Evaluate(new ImportEvaluationInput
        {
            Source = batch.Source,
            Mapping = ImportJsonStore.Mapping(batch.MappingJson) ?? new ColumnMapping(),
            Headers = table.Headers,
            Rows = table.Rows,
            Columns = columns.Select(column => new ImportColumn
            {
                Id = column.Id,
                Name = column.Name,
                Order = column.Order,
                IsDone = column.IsDone,
                IsInProgress = column.IsInProgress
            }).ToList(),
            FallbackColumnId = fallbackColumnId,
            MembersByNormalizedEmail = memberMap,
            NormalizeEmail = email => _normalizer.NormalizeEmail(email) ?? email.Trim().ToUpperInvariant(),
            Existing = existing,
            MaxChildren = _maxChildren
        });
    }

    private async Task ReplaceRowsAsync(
        ImportBatch batch,
        IReadOnlyList<EvaluatedRow> rows,
        IReadOnlyDictionary<int, Guid>? taskIds,
        bool skipInvalid,
        CancellationToken cancellationToken)
    {
        var current = await _db.ImportBatchRows.Where(r => r.ImportBatchId == batch.Id).ToListAsync(cancellationToken);
        _db.ImportBatchRows.RemoveRange(current);
        foreach (var row in rows)
        {
            var status = FinalStatus(row, skipInvalid);
            Guid? taskId = row.ExistingTaskId;
            if (taskIds is not null && taskIds.TryGetValue(row.RowNumber, out var created))
                taskId = created;
            _db.ImportBatchRows.Add(new ImportBatchRow
            {
                Id = Guid.NewGuid(),
                ImportBatchId = batch.Id,
                RowNumber = row.RowNumber,
                ExternalId = TrimTo(row.ExternalId, 200),
                ParentExternalId = TrimTo(row.ParentExternalId, 200),
                Title = TrimTo(row.Title, ImportErrors.TitleMaxLength),
                Description = TrimTo(row.Description, ImportErrors.DescriptionMaxLength),
                ColumnName = row.ColumnName,
                AssigneeEmail = row.AssigneeEmail,
                PriorityText = row.PriorityText,
                DueDateText = row.DueDateText,
                EstimateText = row.EstimateText,
                Status = status,
                ErrorCode = row.ErrorCode,
                WarningCode = row.WarningCode,
                TaskId = taskId
            });
        }

    }

    private static ImportRowStatus FinalStatus(EvaluatedRow row, bool skipInvalid)
    {
        if (row.WillCreate)
            return ImportRowStatus.Created;
        if (skipInvalid && row.ErrorCode is not null)
            return ImportRowStatus.SkippedInvalid;
        return row.Status;
    }

    private async Task<List<ImportRowDto>> ToStoredRowsAsync(
        ImportBatch batch,
        List<ImportBatchRow> rows,
        CancellationToken cancellationToken)
    {
        var taskIds = rows.Where(r => r.TaskId.HasValue).Select(r => r.TaskId!.Value).Distinct().ToList();
        var workspaceId = RequireWorkspace();
        // Display keys for skipped archived cards are hidden by the task filter.
        var tasks = taskIds.Count == 0
            ? []
            : await _db.TaskItems
                .IgnoreQueryFilters()
                .Where(t => taskIds.Contains(t.Id) && t.ProjectId == batch.ProjectId && t.Project.WorkspaceId == workspaceId)
                .Select(t => new { t.Id, t.Number, t.ColumnId, t.AssigneeId, t.Project.Key })
                .ToListAsync(cancellationToken);
        var byId = tasks.ToDictionary(t => t.Id);
        var result = new List<ImportRowDto>(rows.Count);
        foreach (var row in rows)
        {
            string? displayKey = null;
            Guid? columnId = null;
            Guid? assigneeId = null;
            string? columnName = null;
            if (row.TaskId is Guid taskId && byId.TryGetValue(taskId, out var task))
            {
                displayKey = task.Key + "-" + task.Number;
                if (row.Status is ImportRowStatus.Created or ImportRowStatus.Ready or ImportRowStatus.Warning)
                {
                    columnId = task.ColumnId;
                    assigneeId = task.AssigneeId;
                }
            }

            result.Add(new ImportRowDto
            {
                RowNumber = row.RowNumber,
                Title = row.Title,
                ExternalId = row.ExternalId,
                Status = row.Status,
                TaskId = row.TaskId,
                DisplayKey = displayKey,
                ColumnId = columnId,
                ColumnName = columnName,
                AssigneeId = assigneeId,
                ParentExternalId = row.ParentExternalId,
                Error = Problem(row.ErrorCode, ProblemLength(row.ErrorCode), ProblemChildren(row.ErrorCode)),
                Warning = Problem(row.WarningCode, null, ProblemChildren(row.WarningCode))
            });
        }

        return result;
    }

    private ImportBatchDto ToBatch(ImportBatch batch, bool summary)
    {
        return new ImportBatchDto
        {
            Id = batch.Id,
            ProjectId = batch.ProjectId,
            Source = batch.Source,
            Status = batch.Status,
            OriginalFileName = batch.OriginalFileName,
            ExpiresAt = batch.ExpiresAt,
            CreatedAt = batch.CreatedAt,
            CreatedById = batch.CreatedById,
            FallbackColumnId = batch.FallbackColumnId,
            Headers = summary ? [] : ImportJsonStore.Headers(batch.HeadersJson),
            SampleRows = summary || batch.SampleJson is null ? [] : ImportJsonStore.Sample(batch.SampleJson),
            SuggestedMapping = summary ? null : ImportJsonStore.Mapping(batch.SuggestedMappingJson),
            Mapping = ImportJsonStore.Mapping(batch.MappingJson),
            Warnings = ImportJsonStore.Warnings(batch.WarningsJson)
        };
    }

    private static ImportRowDto ToRow(EvaluatedRow row, ImportRowStatus status, Guid? taskId, string? displayKey)
    {
        var showPlan = status is ImportRowStatus.Ready or ImportRowStatus.Warning or ImportRowStatus.Created;
        return new ImportRowDto
        {
            RowNumber = row.RowNumber,
            Title = row.Title,
            ExternalId = row.ExternalId,
            Status = status,
            TaskId = taskId,
            DisplayKey = displayKey,
            ColumnId = showPlan ? row.ColumnId : null,
            ColumnName = showPlan ? row.ResolvedColumnName : null,
            AssigneeId = showPlan ? row.AssigneeId : null,
            ParentExternalId = row.ParentExternalId,
            Error = Problem(row.ErrorCode, row.MaxLength, row.MaxChildren),
            Warning = Problem(row.WarningCode, null, row.WarningCode == ParentTaskErrors.ParentChildLimit ? row.MaxChildren : null)
        };
    }

    private static RowProblem? Problem(string? code, int? maxLength, int? maxChildren) =>
        code is null ? null : new RowProblem { Code = code, MaxLength = maxLength, MaxChildren = maxChildren };

    private static int? ProblemLength(string? code) => code switch
    {
        ImportErrors.TitleTooLong => ImportErrors.TitleMaxLength,
        ImportErrors.DescriptionTooLong => ImportErrors.DescriptionMaxLength,
        _ => null
    };

    private int? ProblemChildren(string? code) =>
        code == ParentTaskErrors.ParentChildLimit ? _maxChildren : null;

    private static List<BatchWarning> WarningsFor(ColumnMapping mapping, ParsedTable table, BatchWarning? parent)
    {
        var warnings = ImportPresets.FieldIgnored(mapping).ToList();
        if (table.SheetCount > 1)
            warnings.Add(new BatchWarning { Code = ImportErrors.ExtraSheets, SheetCount = table.SheetCount });
        if (parent is not null)
            warnings.Add(parent);
        return warnings;
    }

    private static void EnsureHeaders(IReadOnlyList<string> headers, IReadOnlyList<ColumnAssignment> assignments)
    {
        var failures = new List<ValidationFailure>();
        foreach (var assignment in assignments)
        {
            if (!headers.Contains(assignment.Header, StringComparer.Ordinal))
            {
                failures.Add(new ValidationFailure("Assignments", "Header '" + assignment.Header + "' is not in the file.")
                {
                    ErrorCode = "invalid"
                });
            }
        }

        foreach (var header in headers)
        {
            if (!assignments.Any(assignment => assignment.Header == header))
            {
                failures.Add(new ValidationFailure("Assignments", "Header '" + header + "' is not mapped.")
                {
                    ErrorCode = "invalid"
                });
            }
        }

        if (failures.Count > 0)
            throw new ValidationException(failures);
    }

    private async Task LockProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var workspaceId = RequireWorkspace();
        var locked = await _db.Database.SqlQuery<Guid>(
            $"""
            SELECT p."Id" AS "Value"
            FROM "Projects" AS p
            WHERE p."Id" = {projectId} AND p."WorkspaceId" = {workspaceId}
            FOR UPDATE
            """).ToListAsync(cancellationToken);
        if (locked.Count == 0)
            throw new NotFoundException(ImportErrors.NotFoundDetail);
    }

    private async Task LockBatchAsync(Guid projectId, Guid batchId, CancellationToken cancellationToken)
    {
        var workspaceId = RequireWorkspace();
        var locked = await _db.Database.SqlQuery<Guid>(
            $"""
            SELECT b."Id" AS "Value"
            FROM "ImportBatches" AS b
            INNER JOIN "Projects" AS p ON p."Id" = b."ProjectId"
            WHERE b."Id" = {batchId}
              AND b."ProjectId" = {projectId}
              AND p."WorkspaceId" = {workspaceId}
            FOR UPDATE OF b
            """).ToListAsync(cancellationToken);
        if (locked.Count == 0)
            throw new NotFoundException(ImportErrors.NotFoundDetail);
    }

    private Guid RequireWorkspace() =>
        _tenant.WorkspaceId ?? throw new ForbiddenAccessException();

    private static async Task<byte[]> ReadLimitedAsync(Stream content, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, cancellationToken)) > 0)
            buffer.Write(chunk, 0, read);
        return buffer.ToArray();
    }

    private static string SanitizeFileName(string? name)
    {
        var file = Path.GetFileName(name ?? "");
        var cleaned = new string(file.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        if (cleaned.Length == 0)
            cleaned = "import.csv";
        return cleaned.Length <= 200 ? cleaned : cleaned[..200];
    }

    private static string? TrimTo(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];

    private static bool IsImportActivity(string? details, Guid batchId)
    {
        if (string.IsNullOrWhiteSpace(details))
            return false;
        try
        {
            using var document = JsonDocument.Parse(details);
            var root = document.RootElement;
            if (!root.TryGetProperty("source", out var source) || source.GetString() != "import")
                return false;
            return root.TryGetProperty("importBatchId", out var id) && id.TryGetGuid(out var parsed) && parsed == batchId;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
