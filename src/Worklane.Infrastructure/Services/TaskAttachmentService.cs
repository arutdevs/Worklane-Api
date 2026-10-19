using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Worklane.Application.Board.Dtos;
using Worklane.Application.Board.Interfaces;
using Worklane.Application.Common.Exceptions;
using Worklane.Application.Common.Interfaces;
using Worklane.Application.Common.Options;
using Worklane.Application.Workspaces.Interfaces;
using Worklane.Domain.Entities;
using Worklane.Domain.Enums;
using Worklane.Infrastructure.Persistence;

namespace Worklane.Infrastructure.Services;

public class TaskAttachmentService : ITaskAttachmentService
{
    private readonly WorklaneDbContext _db;
    private readonly IFileStorage _storage;
    private readonly IBoardNotifier _notifier;
    private readonly IBoardService _board;
    private readonly IWorkspaceService _workspaces;
    private readonly FileStorageSettings _settings;
    private readonly ILogger<TaskAttachmentService> _logger;

    public TaskAttachmentService(
        WorklaneDbContext db,
        IFileStorage storage,
        IBoardNotifier notifier,
        IBoardService board,
        IWorkspaceService workspaces,
        IOptions<FileStorageSettings> settings,
        ILogger<TaskAttachmentService> logger)
    {
        _db = db;
        _storage = storage;
        _notifier = notifier;
        _board = board;
        _workspaces = workspaces;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<AttachmentDto>> GetAttachmentsAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var exists = await _db.TaskItems.AnyAsync(t => t.Id == taskId, cancellationToken);
        if (!exists)
            throw new NotFoundException(nameof(TaskItem), taskId);

        var files = await _db.TaskAttachments
            .Where(a => a.TaskId == taskId)
            .OrderBy(a => a.CreatedAt)
            .ThenBy(a => a.Id)
            .ToListAsync(cancellationToken);

        var names = await UserLookup.DisplayNamesAsync(_db, files.Select(a => a.UploadedById), cancellationToken);
        return files.Select(a => ToDto(a, names)).ToList();
    }

    public async Task<AttachmentDto> UploadAsync(
        Guid taskId,
        string fileName,
        string? contentType,
        Stream content,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        // contentType is the client header. It is intentionally unused: the extension
        // allow-list and the file's magic bytes decide what this upload is.
        _ = contentType;

        var task = await _db.TaskItems
            .Include(t => t.Project)
            .FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken)
            ?? throw new NotFoundException(nameof(TaskItem), taskId);

        var safeName = SanitizeFileName(fileName);
        var extension = Path.GetExtension(safeName).ToLowerInvariant();
        var detectedType = FileSignature.ContentTypeForExtension(extension);
        if (detectedType is null || !ExtensionAllowed(extension) || !ContentTypeAllowed(detectedType))
            throw new UnsupportedMediaTypeException(extension);

        var headerCapacity = (int)Math.Min(FileSignature.HeaderLength, Math.Max(0, _settings.MaxBytes));
        var header = new byte[headerCapacity];
        var headerRead = 0;
        while (headerRead < header.Length)
        {
            var read = await content.ReadAsync(header.AsMemory(headerRead), cancellationToken);
            if (read == 0) break;
            headerRead += read;
        }

        if (headerRead <= 0)
            throw new BadRequestException("A non-empty file is required.");

        if (headerRead == headerCapacity && headerCapacity == _settings.MaxBytes)
        {
            var extra = new byte[1];
            if (await content.ReadAsync(extra, cancellationToken) > 0)
                throw new PayloadTooLargeException(_settings.MaxBytes);
        }

        if (!FileSignature.Matches(extension, header.AsSpan(0, headerRead)))
            throw new UnsupportedMediaTypeException(extension);

        var workspaceId = task.Project.WorkspaceId;
        // Fast path only. This sum does not see uncommitted uploads, so it is not
        // the quota decision. InsertWithinWorkspaceQuotaAsync holds
        // pg_advisory_xact_lock for this workspace across the re-check and the insert.
        var used = await WorkspaceUsageAsync(workspaceId, cancellationToken);
        if (used >= _settings.WorkspaceQuotaBytes)
            throw new ConflictException("Workspace storage quota exceeded.");

        var attachmentId = Guid.NewGuid();
        var attachment = new TaskAttachment
        {
            Id = attachmentId,
            TaskId = task.Id,
            UploadedById = userId,
            FileName = safeName,
            ContentType = detectedType,
            CreatedAt = DateTimeOffset.UtcNow,
            StorageKey = $"{workspaceId:N}/{task.Id:N}/{attachmentId:N}"
        };

        await using var prefixed = new PrefixedReadStream(header.AsMemory(0, headerRead), content);
        Stream body = prefixed;
        if (StreamingTextChecker.RequiresFullScan(extension))
            body = new ScanningReadStream(prefixed, StreamingTextChecker.Create(extension, header.AsSpan(0, headerRead)));

        var size = await _storage.SaveAsync(attachment.StorageKey, body, _settings.MaxBytes, cancellationToken);
        if (size <= 0)
        {
            await _storage.DeleteAsync(attachment.StorageKey, cancellationToken);
            throw new BadRequestException("A non-empty file is required.");
        }

        try
        {
            await InsertWithinWorkspaceQuotaAsync(attachment, task.Id, userId, size, workspaceId, cancellationToken);
        }
        catch
        {
            await _storage.DeleteAsync(attachment.StorageKey, cancellationToken);
            throw;
        }

        var names = await UserLookup.DisplayNamesAsync(_db, [userId], cancellationToken);
        var dto = ToDto(attachment, names);
        await BroadcastQuietlyAsync(async () =>
        {
            await _notifier.AttachmentAddedAsync(task.ProjectId, Broadcast(dto, task.ProjectId), cancellationToken);
            await BroadcastTaskCountsAsync(task.Id, cancellationToken);
        }, cancellationToken);
        return dto;
    }

    /// <summary>
    /// Authoritative quota check and insert. The advisory lock is held until the
    /// transaction commits, so a second upload in this workspace cannot insert
    /// against a sum that omits this file.
    /// </summary>
    private async Task InsertWithinWorkspaceQuotaAsync(
        TaskAttachment attachment,
        Guid taskId,
        Guid userId,
        long size,
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        await WorkspaceQuotaLock.AcquireAsync(_db.Database, workspaceId, cancellationToken);
        var usedNow = await WorkspaceUsageAsync(workspaceId, cancellationToken);
        if (usedNow + size > _settings.WorkspaceQuotaBytes)
            throw new ConflictException("Workspace storage quota exceeded.");

        attachment.SizeBytes = size;
        _db.TaskAttachments.Add(attachment);
        _db.TaskActivities.Add(new TaskActivity
        {
            Id = Guid.NewGuid(),
            TaskId = taskId,
            Type = TaskActivityType.AttachmentAdded,
            ActorUserId = userId,
            CreatedAt = attachment.CreatedAt,
            Details = ActivityJson.Of(new
            {
                attachmentId = attachment.Id,
                fileName = attachment.FileName,
                contentType = attachment.ContentType,
                sizeBytes = attachment.SizeBytes
            })
        });
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<AttachmentContent> OpenAsync(Guid attachmentId, CancellationToken cancellationToken = default)
    {
        var attachment = await _db.TaskAttachments
            .FirstOrDefaultAsync(a => a.Id == attachmentId, cancellationToken)
            ?? throw new NotFoundException(nameof(TaskAttachment), attachmentId);

        var stream = await _storage.OpenReadAsync(attachment.StorageKey, cancellationToken);
        return new AttachmentContent(stream, attachment.ContentType, attachment.FileName);
    }

    public async Task DeleteAsync(Guid attachmentId, Guid userId, CancellationToken cancellationToken = default)
    {
        var attachment = await _db.TaskAttachments
            .Include(a => a.Task)
            .ThenInclude(t => t.Project)
            .FirstOrDefaultAsync(a => a.Id == attachmentId, cancellationToken)
            ?? throw new NotFoundException(nameof(TaskAttachment), attachmentId);

        if (!await CanModerateAsync(attachment.Task.Project.WorkspaceId, attachment.UploadedById, userId, cancellationToken))
            throw new ForbiddenAccessException("Only the uploader or a workspace admin can delete this file.");

        var projectId = attachment.Task.ProjectId;
        var taskId = attachment.TaskId;
        var storageKey = attachment.StorageKey;
        var now = DateTimeOffset.UtcNow;

        _db.TaskAttachments.Remove(attachment);
        _db.TaskActivities.Add(new TaskActivity
        {
            Id = Guid.NewGuid(),
            TaskId = taskId,
            Type = TaskActivityType.AttachmentDeleted,
            ActorUserId = userId,
            CreatedAt = now,
            Details = ActivityJson.Of(new { attachmentId, fileName = attachment.FileName })
        });
        await _db.SaveChangesAsync(cancellationToken);
        await _storage.DeleteAsync(storageKey, cancellationToken);

        await BroadcastQuietlyAsync(async () =>
        {
            await _notifier.AttachmentDeletedAsync(
                projectId,
                new AttachmentDeletedEvent(projectId, taskId, attachmentId),
                cancellationToken);
            await BroadcastTaskCountsAsync(taskId, cancellationToken);
        }, cancellationToken);
    }

    public async Task<Guid?> GetWorkspaceIdForAttachmentAsync(Guid attachmentId, CancellationToken cancellationToken = default)
    {
        return await _db.TaskAttachments
            .IgnoreQueryFilters()
            .Where(a => a.Id == attachmentId)
            .Select(a => (Guid?)a.Task.Project.WorkspaceId)
            .SingleOrDefaultAsync(cancellationToken);
    }

    private bool ExtensionAllowed(string extension) =>
        _settings.AllowedExtensions.Any(allowed =>
            string.Equals(NormalizeExtension(allowed), extension, StringComparison.Ordinal));

    private bool ContentTypeAllowed(string contentType) =>
        _settings.AllowedContentTypes.Any(allowed =>
            string.Equals(NormalizeContentType(allowed), contentType, StringComparison.Ordinal));

    private async Task<long> WorkspaceUsageAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        return await _db.TaskAttachments
            .IgnoreQueryFilters()
            .Where(a => a.Task.Project.WorkspaceId == workspaceId)
            .SumAsync(a => (long?)a.SizeBytes, cancellationToken) ?? 0;
    }

    private async Task BroadcastTaskCountsAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var task = await _board.GetTaskAsync(taskId, cancellationToken);
        await _notifier.TaskUpdatedAsync(task.ProjectId, task, cancellationToken);
    }

    private async Task BroadcastQuietlyAsync(Func<Task> broadcast, CancellationToken cancellationToken)
    {
        try
        {
            await broadcast();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Attachment change was saved but could not be broadcast.");
        }
    }

    private static string NormalizeExtension(string extension)
    {
        var value = extension.Trim().ToLowerInvariant();
        return value.StartsWith('.') ? value : "." + value;
    }

    internal static string? NormalizeContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return null;
        var type = contentType.Split(';', 2)[0].Trim().ToLowerInvariant();
        return type == "image/jpg" ? "image/jpeg" : type;
    }

    internal static string SanitizeFileName(string? fileName)
    {
        var name = Path.GetFileName(fileName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
            name = "file";

        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');

        name = name.Replace("\"", "_").Replace("\r", "_").Replace("\n", "_");
        return name.Length <= 255 ? name : name[^255..];
    }

    private async Task<bool> CanModerateAsync(Guid workspaceId, Guid authorId, Guid userId, CancellationToken cancellationToken)
    {
        var role = await _workspaces.GetUserRoleAsync(workspaceId, userId, cancellationToken);
        if (role is WorkspaceRole.Admin or WorkspaceRole.Owner)
            return true;
        return authorId == userId && role == WorkspaceRole.Member;
    }

    /// <summary>Replays the header that was already read, then the unread remainder of the upload.</summary>
    private sealed class PrefixedReadStream : Stream
    {
        private readonly ReadOnlyMemory<byte> _prefix;
        private readonly Stream _rest;
        private int _prefixOffset;

        public PrefixedReadStream(ReadOnlyMemory<byte> prefix, Stream rest)
        {
            _prefix = prefix;
            _rest = rest;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_prefixOffset < _prefix.Length)
            {
                var count = Math.Min(buffer.Length, _prefix.Length - _prefixOffset);
                _prefix.Slice(_prefixOffset, count).CopyTo(buffer);
                _prefixOffset += count;
                return count;
            }

            return await _rest.ReadAsync(buffer, cancellationToken);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static AttachmentDto ToDto(TaskAttachment attachment, IReadOnlyDictionary<Guid, string> names) =>
        new(
            attachment.Id,
            attachment.TaskId,
            attachment.FileName,
            attachment.ContentType,
            attachment.SizeBytes,
            UserLookup.Ref(names, attachment.UploadedById),
            attachment.CreatedAt);

    private static AttachmentBroadcastDto Broadcast(AttachmentDto dto, Guid projectId) =>
        new(dto.Id, dto.TaskId, dto.FileName, dto.ContentType, dto.SizeBytes, dto.UploadedBy, dto.CreatedAt, projectId);
}
