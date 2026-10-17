using Worklane.Application.Common;
using Worklane.Domain.Enums;

namespace Worklane.Application.Board.Dtos;

public record BoardColumnDto(
    Guid Id,
    Guid ProjectId,
    string Name,
    int Order,
    bool IsDone,
    bool IsInProgress,
    IReadOnlyList<TaskItemDto> Tasks,
    int? WipLimit,
    int TaskCount,
    bool IsOverWipLimit
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
    bool IsArchived,

    // ── Work metrics ──────────────────────────────────────────────────────────
    // Derived server-side so every client shows the same numbers.

    /// <summary>Planned effort in man-days, null when unestimated.</summary>
    double? EstimateDays,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,

    /// <summary>
    /// Working days elapsed since the task started — still climbing while it is in
    /// progress, frozen once it reaches a done column. Null until it starts.
    /// </summary>
    double? ActualDays,
    bool IsOverEstimate,
    double DaysOverEstimate,
    bool IsOverdue,
    int DaysOverdue,

    /// <summary>
    /// True when the task's current column has a WIP limit and more cards than that
    /// limit. Create and move responses report the destination column.
    /// </summary>
    bool IsOverWipLimit
);

public record CreateColumnRequest(string Name, int? WipLimit = null);

public record UpdateColumnRequest(
    string? Name,
    int? Order,
    bool? IsDone,
    bool? IsInProgress,
    Optional<int?> WipLimit);

public record CreateTaskRequest(
    string Title,
    string? Description,
    Guid? AssigneeId,
    TaskPriority Priority,
    DateTimeOffset? DueDate,
    Guid? ColumnId,
    double? EstimateDays
);

public record UpdateTaskRequest(
    string? Title,
    string? Description,
    Guid? AssigneeId,
    TaskPriority? Priority,
    DateTimeOffset? DueDate,
    Guid? ColumnId,
    double? EstimateDays
);

public record MoveTaskRequest(Guid ToColumnId, int ToIndex);

/// <summary>A task assigned to the current user, with just enough of its project/column
/// context to render and link to it from a cross-project "My tasks" list.</summary>
public record MyTaskDto(
    TaskItemDto Task,
    string ProjectKey,
    string ProjectName,
    string ColumnName,
    bool IsColumnDone
);
