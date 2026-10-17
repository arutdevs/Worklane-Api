using System.Text.Json;
using Worklane.Domain.Enums;

namespace Worklane.Application.Board.Dtos;

/// <summary>A person attached to an activity, comment, or file. JSON: userId, displayName.</summary>
public record UserRef(Guid UserId, string DisplayName);

public record TaskActivityDto(
    Guid Id,
    Guid TaskId,
    TaskActivityType Type,
    UserRef Actor,
    DateTimeOffset CreatedAt,
    JsonElement? Details);

public record CommentDto(
    Guid Id,
    Guid TaskId,
    UserRef Author,
    string Body,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

/// <summary>SignalR <c>CommentCreated</c> / <c>CommentUpdated</c> payload: a comment plus its project.</summary>
public record CommentBroadcastDto(
    Guid Id,
    Guid TaskId,
    UserRef Author,
    string Body,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    Guid ProjectId);

public record CommentDeletedEvent(Guid ProjectId, Guid TaskId, Guid CommentId);

public record CreateCommentRequest(string Body);

public record UpdateCommentRequest(string Body);

public record AttachmentDto(
    Guid Id,
    Guid TaskId,
    string FileName,
    string ContentType,
    long SizeBytes,
    UserRef UploadedBy,
    DateTimeOffset CreatedAt);

/// <summary>SignalR <c>AttachmentAdded</c> payload: an attachment plus its project.</summary>
public record AttachmentBroadcastDto(
    Guid Id,
    Guid TaskId,
    string FileName,
    string ContentType,
    long SizeBytes,
    UserRef UploadedBy,
    DateTimeOffset CreatedAt,
    Guid ProjectId);

public record AttachmentDeletedEvent(Guid ProjectId, Guid TaskId, Guid AttachmentId);
