using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Worklane.Application.Common.Exceptions;
using Worklane.Application.Common.Interfaces;
using Worklane.Application.Common.Options;
using Worklane.Application.Imports.Dtos;
using Worklane.Application.Imports.Interfaces;
using Worklane.Domain.Enums;
using Worklane.Domain.Imports;
using Worklane.WebApi.Filters;

namespace Worklane.WebApi.Controllers;

[ApiController]
[Route("api")]
[Authorize]
public class ImportsController : ControllerBase
{
    private readonly IImportService _imports;
    private readonly IProjectExportService _exports;
    private readonly ICurrentUserService _currentUser;
    private readonly IValidator<UpdateImportMappingRequest> _mappingValidator;
    private readonly IValidator<CommitImportRequest> _commitValidator;
    private readonly IValidator<ExportTasksRequest> _exportValidator;

    public ImportsController(
        IImportService imports,
        IProjectExportService exports,
        ICurrentUserService currentUser,
        IValidator<UpdateImportMappingRequest> mappingValidator,
        IValidator<CommitImportRequest> commitValidator,
        IValidator<ExportTasksRequest> exportValidator)
    {
        _imports = imports;
        _exports = exports;
        _currentUser = currentUser;
        _mappingValidator = mappingValidator;
        _commitValidator = commitValidator;
        _exportValidator = exportValidator;
    }

    [HttpPost("projects/{projectId:guid}/imports")]
    [Consumes("multipart/form-data")]
    [EnableRateLimiting(ImportUploadRateLimitSettings.PolicyName)]
    [ImportUploadLimit]
    [RequireWorkspaceRole(WorkspaceRole.Member, WorkspaceResourceType.Project, "projectId")]
    public async Task<ActionResult<ImportBatchDto>> Upload(Guid projectId, CancellationToken cancellationToken)
    {
        var form = await Request.ReadFormAsync(cancellationToken);
        var file = form.Files.GetFile("file");
        if (file is null || file.Length <= 0)
            throw new BadRequestException("file_empty", "A non-empty file is required.");

        await using var stream = file.OpenReadStream();
        var batch = await _imports.UploadAsync(
            projectId,
            _currentUser.UserId,
            form["preset"].ToString(),
            file.FileName,
            stream,
            cancellationToken);
        return Created($"/api/projects/{projectId}/imports/{batch.Id}", batch);
    }

    [HttpPut("projects/{projectId:guid}/imports/{importId:guid}/mapping")]
    [EnableRateLimiting(ImportPreviewRateLimitSettings.PolicyName)]
    [RequireWorkspaceRole(WorkspaceRole.Member, WorkspaceResourceType.Project, "projectId")]
    public async Task<ActionResult<ImportBatchDto>> UpdateMapping(
        Guid projectId,
        Guid importId,
        UpdateImportMappingRequest request,
        CancellationToken cancellationToken)
    {
        await _mappingValidator.ValidateAndThrowAsync(request, cancellationToken);
        var batch = await _imports.UpdateMappingAsync(projectId, importId, request, cancellationToken);
        return Ok(batch);
    }

    [HttpPost("projects/{projectId:guid}/imports/{importId:guid}/preview")]
    [EnableRateLimiting(ImportPreviewRateLimitSettings.PolicyName)]
    [RequireWorkspaceRole(WorkspaceRole.Member, WorkspaceResourceType.Project, "projectId")]
    public async Task<ActionResult<ImportPreviewDto>> Preview(Guid projectId, Guid importId, CancellationToken cancellationToken)
    {
        var preview = await _imports.PreviewAsync(projectId, importId, cancellationToken);
        return Ok(preview);
    }

    [HttpPost("projects/{projectId:guid}/imports/{importId:guid}/commit")]
    [EnableRateLimiting(ImportCommitRateLimitSettings.PolicyName)]
    [RequireWorkspaceRole(WorkspaceRole.Member, WorkspaceResourceType.Project, "projectId")]
    public async Task<ActionResult<ImportCommitDto>> Commit(
        Guid projectId,
        Guid importId,
        CommitImportRequest request,
        CancellationToken cancellationToken)
    {
        await _commitValidator.ValidateAndThrowAsync(request, cancellationToken);
        var result = await _imports.CommitAsync(projectId, importId, _currentUser.UserId, request, cancellationToken);
        return Ok(result);
    }

    [HttpGet("projects/{projectId:guid}/imports/{importId:guid}")]
    [RequireWorkspaceRole(WorkspaceRole.Viewer, WorkspaceResourceType.Project, "projectId")]
    public async Task<ActionResult<ImportPreviewDto>> Get(Guid projectId, Guid importId, CancellationToken cancellationToken)
    {
        var preview = await _imports.GetAsync(projectId, importId, cancellationToken);
        return Ok(preview);
    }

    [HttpGet("projects/{projectId:guid}/imports")]
    [RequireWorkspaceRole(WorkspaceRole.Viewer, WorkspaceResourceType.Project, "projectId")]
    public async Task<ActionResult<IReadOnlyList<ImportBatchDto>>> List(Guid projectId, CancellationToken cancellationToken)
    {
        var batches = await _imports.ListAsync(projectId, cancellationToken);
        return Ok(batches);
    }

    [HttpPost("projects/{projectId:guid}/imports/{importId:guid}/undo")]
    [EnableRateLimiting(ImportCommitRateLimitSettings.PolicyName)]
    [RequireWorkspaceRole(WorkspaceRole.Admin, WorkspaceResourceType.Project, "projectId")]
    public async Task<ActionResult<ImportUndoDto>> Undo(Guid projectId, Guid importId, CancellationToken cancellationToken)
    {
        var result = await _imports.UndoAsync(projectId, importId, cancellationToken);
        return Ok(result);
    }

    [HttpGet("projects/{projectId:guid}/tasks/export")]
    [EnableRateLimiting(ExportRateLimitSettings.PolicyName)]
    [RequireWorkspaceRole(WorkspaceRole.Viewer, WorkspaceResourceType.Project, "projectId")]
    public async Task<IActionResult> Export(Guid projectId, [FromQuery] ExportTasksRequest request, CancellationToken cancellationToken)
    {
        await _exportValidator.ValidateAndThrowAsync(request, cancellationToken);
        var format = ImportWire.ParseFormat(request.Format!);
        var file = await _exports.ExportAsync(projectId, _currentUser.UserId, format, cancellationToken);
        Response.Headers.ContentDisposition = "attachment; filename=\"" + file.FileName + "\"";
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(file.Content, file.ContentType);
    }
}
