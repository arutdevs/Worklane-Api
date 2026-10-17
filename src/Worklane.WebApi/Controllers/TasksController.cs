using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Worklane.Application.Board.Dtos;
using Worklane.Application.Board.Interfaces;
using Worklane.Application.Common.Interfaces;
using Worklane.Domain.Enums;
using Worklane.WebApi.Filters;

namespace Worklane.WebApi.Controllers;

[ApiController]
[Authorize]
public class TasksController : ControllerBase
{
    private readonly IBoardService _board;
    private readonly ICurrentUserService _currentUser;

    public TasksController(IBoardService board, ICurrentUserService currentUser)
    {
        _board = board;
        _currentUser = currentUser;
    }

    /// <summary>Returns filtered tasks for a project (flat list, not grouped by column).</summary>
    [HttpGet("api/projects/{projectId:guid}/tasks")]
    [RequireWorkspaceRole(WorkspaceRole.Viewer, WorkspaceResourceType.Project, "projectId")]
    public async Task<ActionResult<IReadOnlyList<TaskItemDto>>> GetTasks(
        Guid projectId,
        [FromQuery] Guid? assigneeId,
        [FromQuery] TaskPriority? priority,
        [FromQuery] string? search,
        CancellationToken ct)
    {
        var result = await _board.GetTasksAsync(projectId, assigneeId, priority, search, ct);
        return Ok(result);
    }

    /// <summary>Creates a task in the first column (or the specified column).</summary>
    [HttpPost("api/projects/{projectId:guid}/tasks")]
    [RequireWorkspaceRole(WorkspaceRole.Member, WorkspaceResourceType.Project, "projectId")]
    public async Task<ActionResult<TaskItemDto>> CreateTask(
        Guid projectId,
        [FromBody] CreateTaskRequest request,
        [FromServices] IValidator<CreateTaskRequest> validator,
        CancellationToken ct)
    {
        await validator.ValidateAndThrowAsync(request, ct);
        var result = await _board.CreateTaskAsync(projectId, request, _currentUser.UserId, ct);
        return CreatedAtAction(nameof(GetTask), new { taskId = result.Id }, result);
    }

    /// <summary>Returns a single task by id.</summary>
    [HttpGet("api/tasks/{taskId:guid}")]
    [RequireWorkspaceRole(WorkspaceRole.Viewer, WorkspaceResourceType.Task, "taskId")]
    public async Task<ActionResult<TaskItemDto>> GetTask(Guid taskId, CancellationToken ct)
    {
        var result = await _board.GetTaskAsync(taskId, ct);
        return Ok(result);
    }

    /// <summary>Partially updates any editable field on a task.</summary>
    [HttpPatch("api/tasks/{taskId:guid}")]
    [RequireWorkspaceRole(WorkspaceRole.Member, WorkspaceResourceType.Task, "taskId")]
    public async Task<ActionResult<TaskItemDto>> UpdateTask(
        Guid taskId,
        [FromBody] UpdateTaskRequest request,
        [FromServices] IValidator<UpdateTaskRequest> validator,
        CancellationToken ct)
    {
        await validator.ValidateAndThrowAsync(request, ct);
        var result = await _board.UpdateTaskAsync(taskId, request, _currentUser.UserId, ct);
        return Ok(result);
    }

    /// <summary>
    /// Moves a task to a column at a given index using the fractional-order strategy.
    /// Triggers rebalance when remaining precision drops below the minimum gap.
    /// </summary>
    [HttpPost("api/tasks/{taskId:guid}/move")]
    [RequireWorkspaceRole(WorkspaceRole.Member, WorkspaceResourceType.Task, "taskId")]
    public async Task<ActionResult<TaskItemDto>> MoveTask(
        Guid taskId,
        [FromBody] MoveTaskRequest request,
        [FromServices] IValidator<MoveTaskRequest> validator,
        CancellationToken ct)
    {
        await validator.ValidateAndThrowAsync(request, ct);
        var result = await _board.MoveTaskAsync(taskId, request, _currentUser.UserId, ct);
        return Ok(result);
    }

    /// <summary>Returns every task assigned to the current user across all projects in a workspace.</summary>
    [HttpGet("api/workspaces/{workspaceId:guid}/my-tasks")]
    [RequireWorkspaceRole(WorkspaceRole.Viewer, WorkspaceResourceType.Workspace, "workspaceId")]
    public async Task<ActionResult<IReadOnlyList<MyTaskDto>>> GetMyTasks(Guid workspaceId, CancellationToken ct)
    {
        var result = await _board.GetMyTasksAsync(workspaceId, _currentUser.UserId, ct);
        return Ok(result);
    }

    /// <summary>Archives (soft-deletes) a task so it no longer appears on the board.</summary>
    [HttpDelete("api/tasks/{taskId:guid}")]
    [RequireWorkspaceRole(WorkspaceRole.Member, WorkspaceResourceType.Task, "taskId")]
    public async Task<IActionResult> ArchiveTask(Guid taskId, CancellationToken ct)
    {
        await _board.ArchiveTaskAsync(taskId, ct);
        return NoContent();
    }
}
