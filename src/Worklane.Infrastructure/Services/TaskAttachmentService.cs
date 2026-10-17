using Microsoft.EntityFrameworkCore;
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
    private readonly IWorkspaceService _workspaces;
    private readonly FileStorageSettings _settings;

    public TaskAttachmentService(
        WorklaneDbContext db,
        IFileStorage storage,
        IBoardNotifier notifier,
        IWorkspaceService workspaces,
        IOptions<FileStorageSettings> settings)
    {
        _db = db;
        _storage = storage;
        _notifier = notifier;
        _workspaces = workspaces;
        _settings = settings.Value;
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
        var task = await _db.TaskItems
            .Include(t => t.Project)
            .FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken)
            ?? throw new NotFoundException(nameof(TaskItem), taskId);

        var normalized = NormalizeContentType(contentType);
        if (normalized is null || !Allowed(normalized))
            throw new UnsupportedMediaTypeException(contentType);

        if (content.CanSeek && content.Length > _settings.MaxBytes)
            throw new PayloadTooLargeException(_settings.MaxBytes);

        var safeName = SanitizeFileName(fileName);
        var attachmentId = Guid.NewGuid();
        var attachment = new TaskAttachment
        {
            Id = attachmentId,
            TaskId = task.Id,
            UploadedById = userId,
            FileName = safeName,
            ContentType = normalized,
            CreatedAt = DateTimeOffset.UtcNow,
            StorageKey = $"{task.Project.WorkspaceId:N}/{task.Id:N}/{attachmentId:N}"
        };

        var size = await _storage.SaveAsync(attachment.StorageKey, content, _settings.MaxBytes, cancellationToken);
        if (size <= 0)
        {
            await _storage.DeleteAsync(attachment.StorageKey, cancellationToken);
            throw new BadRequestException("A non-empty file is required.");
        }

        attachment.SizeBytes = size;
        _db.TaskAttachments.Add(attachment);
        _db.TaskActivities.Add(new TaskActivity
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
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

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await _storage.DeleteAsync(attachment.StorageKey, cancellationToken);
            throw;
        }

        var names = await UserLookup.DisplayNamesAsync(_db, [userId], cancellationToken);
        var dto = ToDto(attachment, names);
        await _notifier.AttachmentAddedAsync(task.ProjectId, Broadcast(dto, task.ProjectId), cancellationToken);
        return dto;
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

        await _notifier.AttachmentDeletedAsync(
            projectId,
            new AttachmentDeletedEvent(projectId, taskId, attachmentId),
            cancellationToken);
    }

    public async Task<Guid?> GetWorkspaceIdForAttachmentAsync(Guid attachmentId, CancellationToken cancellationToken = default)
    {
        return await _db.TaskAttachments
            .IgnoreQueryFilters()
            .Where(a => a.Id == attachmentId)
            .Select(a => (Guid?)a.Task.Project.WorkspaceId)
            .SingleOrDefaultAsync(cancellationToken);
    }

    private bool Allowed(string contentType) =>
        _settings.AllowedContentTypes.Any(allowed =>
            string.Equals(NormalizeContentType(allowed), contentType, StringComparison.Ordinal));

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
        if (authorId == userId) return true;
        var role = await _workspaces.GetUserRoleAsync(workspaceId, userId, cancellationToken);
        return role is WorkspaceRole.Admin or WorkspaceRole.Owner;
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
