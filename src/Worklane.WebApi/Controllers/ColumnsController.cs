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
public class ColumnsController : ControllerBase
{
    private readonly IBoardService _board;
    private readonly ICurrentUserService _currentUser;

    public ColumnsController(IBoardService board, ICurrentUserService currentUser)
    {
        _board = board;
        _currentUser = currentUser;
    }

    /// <summary>Returns all columns for a project, each with their ordered tasks.</summary>
    [HttpGet("api/projects/{projectId:guid}/columns")]
    [RequireWorkspaceRole(WorkspaceRole.Viewer, WorkspaceResourceType.Project, "projectId")]
    public async Task<ActionResult<IReadOnlyList<BoardColumnDto>>> GetColumns(
        Guid projectId, CancellationToken ct)
    {
        var result = await _board.GetColumnsAsync(projectId, ct);
        return Ok(result);
    }

    /// <summary>Creates a new column at the end of the project's column list.</summary>
    [HttpPost("api/projects/{projectId:guid}/columns")]
    [RequireWorkspaceRole(WorkspaceRole.Member, WorkspaceResourceType.Project, "projectId")]
    public async Task<ActionResult<BoardColumnDto>> CreateColumn(
        Guid projectId,
        [FromBody] CreateColumnRequest request,
        [FromServices] IValidator<CreateColumnRequest> validator,
        CancellationToken ct)
    {
        await validator.ValidateAndThrowAsync(request, ct);
        var result = await _board.CreateColumnAsync(projectId, request, _currentUser.UserId, ct);
        return CreatedAtAction(nameof(GetColumns), new { projectId }, result);
    }

    /// <summary>Updates column name and/or order position.</summary>
    [HttpPatch("api/columns/{columnId:guid}")]
    [RequireWorkspaceRole(WorkspaceRole.Member, WorkspaceResourceType.Column, "columnId")]
    public async Task<ActionResult<BoardColumnDto>> UpdateColumn(
        Guid columnId,
        [FromBody] UpdateColumnRequest request,
        [FromServices] IValidator<UpdateColumnRequest> validator,
        CancellationToken ct)
    {
        await validator.ValidateAndThrowAsync(request, ct);
        var result = await _board.UpdateColumnAsync(columnId, request, ct);
        return Ok(result);
    }

    /// <summary>
    /// Deletes a column. If the column still contains tasks, targetColumnId must be
    /// provided; all tasks are moved there before deletion.
    /// </summary>
    [HttpDelete("api/columns/{columnId:guid}")]
    [RequireWorkspaceRole(WorkspaceRole.Admin, WorkspaceResourceType.Column, "columnId")]
    public async Task<IActionResult> DeleteColumn(
        Guid columnId,
        [FromQuery] Guid? targetColumnId,
        CancellationToken ct)
    {
        await _board.DeleteColumnAsync(columnId, targetColumnId, ct);
        return NoContent();
    }
}
