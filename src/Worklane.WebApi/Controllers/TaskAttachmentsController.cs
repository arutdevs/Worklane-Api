using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Worklane.Application.Board.Dtos;
using Worklane.Application.Board.Interfaces;
using Worklane.Application.Common.Exceptions;
using Worklane.Application.Common.Interfaces;
using Worklane.Application.Common.Options;
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
    /// Uploads a file (multipart field <c>file</c>). 413 when it exceeds the transport
    /// or per-file cap, 409 when the workspace quota is full, 415 when the type is not allowed.
    /// </summary>
    [HttpPost("api/tasks/{taskId:guid}/attachments")]
    [Consumes("multipart/form-data")]
    [EnableRateLimiting("attachments")]
    [RequestSizeLimit(FileStorageSettings.AttachmentRequestLimitBytes)]
    [RequireWorkspaceRole(WorkspaceRole.Member, WorkspaceResourceType.Task, "taskId")]
    public async Task<ActionResult<AttachmentDto>> Upload(Guid taskId, CancellationToken ct)
    {
        // Read the form here so a Kestrel body-size rejection is not turned into a
        // model-state 400 by IFormFile binding. The size limit is the endpoint's.
        var form = await Request.ReadFormAsync(ct);
        var file = form.Files.GetFile("file");
        if (file is null || file.Length <= 0)
            throw new BadRequestException("A non-empty file is required.");

        await using var stream = file.OpenReadStream();
        var result = await _attachments.UploadAsync(taskId, file.FileName, file.ContentType, stream, _currentUser.UserId, ct);
        return Created($"/api/attachments/{result.Id}/download", result);
    }

    /// <summary>
    /// Downloads a file. The body is always application/octet-stream: the stored type
    /// is not echoed, and the browser is told not to sniff it.
    /// </summary>
    [HttpGet("api/attachments/{attachmentId:guid}/download")]
    [RequireWorkspaceRole(WorkspaceRole.Viewer, WorkspaceResourceType.Attachment, "attachmentId")]
    public async Task<IActionResult> Download(Guid attachmentId, CancellationToken ct)
    {
        var file = await _attachments.OpenAsync(attachmentId, ct);
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        return File(file.Content, "application/octet-stream", file.FileName);
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
