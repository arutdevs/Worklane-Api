using Worklane.Domain.Enums;

namespace Worklane.Application.Board.Dtos;

public record BoardColumnDto(
    Guid Id,
    Guid ProjectId,
    string Name,
    int Order,
    bool IsDone,
    IReadOnlyList<TaskItemDto> Tasks
);

public record TaskItemDto(
    Guid Id,
    Guid ProjectId,
    Guid ColumnId,
    int Number,
    string DisplayKey,
    string Title,
    string? Description,
    Guid? AssigneeId,
    string? AssigneeName,
    string? AssigneeAvatarUrl,
    TaskPriority Priority,
    DateTimeOffset? DueDate,
    double Order,
    Guid CreatedById,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool IsArchived
);

public record CreateColumnRequest(string Name);

public record UpdateColumnRequest(string? Name, int? Order);

public record CreateTaskRequest(
    string Title,
    string? Description,
    Guid? AssigneeId,
    TaskPriority Priority,
    DateTimeOffset? DueDate,
    Guid? ColumnId
);

public record UpdateTaskRequest(
    string? Title,
    string? Description,
    Guid? AssigneeId,
    TaskPriority? Priority,
    DateTimeOffset? DueDate,
    Guid? ColumnId
);

public record MoveTaskRequest(Guid ToColumnId, int ToIndex);
