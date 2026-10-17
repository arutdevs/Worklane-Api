using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Worklane.Application.Board.Dtos;
using Worklane.Application.Board.Interfaces;
using Worklane.Application.Common.Exceptions;
using Worklane.Application.Common.Interfaces;
using Worklane.Domain.Enums;
using Worklane.WebApi.Filters;

namespace Worklane.WebApi.Controllers;

[ApiController]
[Authorize]
public class TaskAttachmentsController : ControllerBase
{
    private readonly ITaskAttachmentService _attachments;
    private readonly ICurrentUserService _currentUser;

    public TaskAttachmentsController(ITaskAttachmentService attachments, ICurrentUserService currentUser)
    {
        _attachments = attachments;
        _currentUser = currentUser;
    }

    /// <summary>Files on a task, oldest first.</summary>
    [HttpGet("api/tasks/{taskId:guid}/attachments")]
    [RequireWorkspaceRole(WorkspaceRole.Viewer, WorkspaceResourceType.Task, "taskId")]
    public async Task<ActionResult<IReadOnlyList<AttachmentDto>>> GetAttachments(Guid taskId, CancellationToken ct)
    {
        var result = await _attachments.GetAttachmentsAsync(taskId, ct);
        return Ok(result);
    }

    /// <summary>
    /// Uploads a file (multipart field <c>file</c>). 413 when it exceeds the configured
    /// maximum, 415 when the content type is not on the allow-list.
    /// </summary>
    [HttpPost("api/tasks/{taskId:guid}/attachments")]
    [Consumes("multipart/form-data")]
    [RequireWorkspaceRole(WorkspaceRole.Member, WorkspaceResourceType.Task, "taskId")]
    public async Task<ActionResult<AttachmentDto>> Upload(Guid taskId, IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length <= 0)
            throw new BadRequestException("A non-empty file is required.");

        await using var stream = file.OpenReadStream();
        var result = await _attachments.UploadAsync(taskId, file.FileName, file.ContentType, stream, _currentUser.UserId, ct);
        return Created($"/api/attachments/{result.Id}/download", result);
    }

    /// <summary>Downloads a file. The workspace is resolved from the attachment, not from a client-supplied path.</summary>
    [HttpGet("api/attachments/{attachmentId:guid}/download")]
    [RequireWorkspaceRole(WorkspaceRole.Viewer, WorkspaceResourceType.Attachment, "attachmentId")]
    public async Task<IActionResult> Download(Guid attachmentId, CancellationToken ct)
    {
        var file = await _attachments.OpenAsync(attachmentId, ct);
        return File(file.Content, file.ContentType, file.FileName);
    }

    /// <summary>Deletes a file. The uploader, or a workspace admin or owner, may delete.</summary>
    [HttpDelete("api/attachments/{attachmentId:guid}")]
    [RequireWorkspaceRole(WorkspaceRole.Member, WorkspaceResourceType.Attachment, "attachmentId")]
    public async Task<IActionResult> Delete(Guid attachmentId, CancellationToken ct)
    {
        await _attachments.DeleteAsync(attachmentId, _currentUser.UserId, ct);
        return NoContent();
    }
}
