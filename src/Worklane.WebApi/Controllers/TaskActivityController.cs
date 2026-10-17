using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Worklane.Application.Board.Dtos;
using Worklane.Application.Board.Interfaces;
using Worklane.Domain.Enums;
using Worklane.WebApi.Filters;

namespace Worklane.WebApi.Controllers;

[ApiController]
[Authorize]
public class TaskActivityController : ControllerBase
{
    private readonly ITaskActivityService _activity;

    public TaskActivityController(ITaskActivityService activity)
    {
        _activity = activity;
    }

    /// <summary>History of a task, newest first.</summary>
    [HttpGet("api/tasks/{taskId:guid}/activity")]
    [RequireWorkspaceRole(WorkspaceRole.Viewer, WorkspaceResourceType.Task, "taskId")]
    public async Task<ActionResult<IReadOnlyList<TaskActivityDto>>> GetActivity(Guid taskId, CancellationToken ct)
    {
        var result = await _activity.GetActivityAsync(taskId, ct);
        return Ok(result);
    }
}
