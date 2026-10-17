using Worklane.Application.Board.Dtos;
using Worklane.Domain.Enums;

namespace Worklane.Application.Board.Interfaces;

public interface IBoardService
{
    // Columns
    Task<IReadOnlyList<BoardColumnDto>> GetColumnsAsync(Guid projectId, CancellationToken ct = default);
    Task<BoardColumnDto> CreateColumnAsync(Guid projectId, CreateColumnRequest request, Guid userId, CancellationToken ct = default);
    Task<BoardColumnDto> UpdateColumnAsync(Guid columnId, UpdateColumnRequest request, CancellationToken ct = default);
    Task DeleteColumnAsync(Guid columnId, Guid? targetColumnId, CancellationToken ct = default);

    // Tasks
    Task<TaskItemDto> CreateTaskAsync(Guid projectId, CreateTaskRequest request, Guid userId, CancellationToken ct = default);
    Task<TaskItemDto> GetTaskAsync(Guid taskId, CancellationToken ct = default);
    Task<TaskItemDto> UpdateTaskAsync(Guid taskId, UpdateTaskRequest request, Guid userId, CancellationToken ct = default);
    Task<TaskItemDto> MoveTaskAsync(Guid taskId, MoveTaskRequest request, Guid userId, CancellationToken ct = default);
    Task ArchiveTaskAsync(Guid taskId, CancellationToken ct = default);
    Task<IReadOnlyList<TaskItemDto>> GetTasksAsync(Guid projectId, Guid? assigneeId, TaskPriority? priority, string? search, CancellationToken ct = default);
    Task<IReadOnlyList<MyTaskDto>> GetMyTasksAsync(Guid workspaceId, Guid userId, CancellationToken ct = default);

    // Auth-filter helpers
    Task<Guid?> GetWorkspaceIdForColumnAsync(Guid columnId, CancellationToken ct = default);
    Task<Guid?> GetWorkspaceIdForTaskAsync(Guid taskId, CancellationToken ct = default);
}
