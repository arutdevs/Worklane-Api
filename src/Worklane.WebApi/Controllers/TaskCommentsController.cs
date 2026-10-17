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
public class TaskCommentsController : ControllerBase
{
    private readonly ITaskCommentService _comments;
    private readonly ICurrentUserService _currentUser;

    public TaskCommentsController(ITaskCommentService comments, ICurrentUserService currentUser)
    {
        _comments = comments;
        _currentUser = currentUser;
    }

    /// <summary>Comments on a task, oldest first.</summary>
    [HttpGet("api/tasks/{taskId:guid}/comments")]
    [RequireWorkspaceRole(WorkspaceRole.Viewer, WorkspaceResourceType.Task, "taskId")]
    public async Task<ActionResult<IReadOnlyList<CommentDto>>> GetComments(Guid taskId, CancellationToken ct)
    {
        var result = await _comments.GetCommentsAsync(taskId, ct);
        return Ok(result);
    }

    /// <summary>Adds a comment. Broadcasts <c>CommentCreated</c> to the project group.</summary>
    [HttpPost("api/tasks/{taskId:guid}/comments")]
    [RequireWorkspaceRole(WorkspaceRole.Member, WorkspaceResourceType.Task, "taskId")]
    public async Task<ActionResult<CommentDto>> CreateComment(
        Guid taskId,
        [FromBody] CreateCommentRequest request,
        [FromServices] IValidator<CreateCommentRequest> validator,
        CancellationToken ct)
    {
        await validator.ValidateAndThrowAsync(request, ct);
        var result = await _comments.CreateCommentAsync(taskId, request, _currentUser.UserId, ct);
        return Created($"/api/tasks/{taskId}/comments/{result.Id}", result);
    }

    /// <summary>Edits a comment. Only the author may edit.</summary>
    [HttpPatch("api/comments/{commentId:guid}")]
    [RequireWorkspaceRole(WorkspaceRole.Member, WorkspaceResourceType.Comment, "commentId")]
    public async Task<ActionResult<CommentDto>> UpdateComment(
        Guid commentId,
        [FromBody] UpdateCommentRequest request,
        [FromServices] IValidator<UpdateCommentRequest> validator,
        CancellationToken ct)
    {
        await validator.ValidateAndThrowAsync(request, ct);
        var result = await _comments.UpdateCommentAsync(commentId, request, _currentUser.UserId, ct);
        return Ok(result);
    }

    /// <summary>Deletes a comment. The author, or a workspace admin or owner, may delete.</summary>
    [HttpDelete("api/comments/{commentId:guid}")]
    [RequireWorkspaceRole(WorkspaceRole.Member, WorkspaceResourceType.Comment, "commentId")]
    public async Task<IActionResult> DeleteComment(Guid commentId, CancellationToken ct)
    {
        await _comments.DeleteCommentAsync(commentId, _currentUser.UserId, ct);
        return NoContent();
    }
}
