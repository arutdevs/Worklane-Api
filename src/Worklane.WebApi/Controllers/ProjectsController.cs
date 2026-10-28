using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Worklane.Application.Common.Interfaces;
using Worklane.Application.Projects.Dtos;
using Worklane.Application.Projects.Interfaces;
using Worklane.Domain.Enums;
using Worklane.WebApi.Filters;

namespace Worklane.WebApi.Controllers;

[ApiController]
[Route("api")]
[Authorize]
public class ProjectsController : ControllerBase
{
    private readonly IProjectService _projectService;
    private readonly ICurrentUserService _currentUser;
    private readonly IValidator<CreateProjectRequest> _createValidator;
    private readonly IValidator<UpdateProjectRequest> _updateValidator;

    public ProjectsController(
        IProjectService projectService,
        ICurrentUserService currentUser,
        IValidator<CreateProjectRequest> createValidator,
        IValidator<UpdateProjectRequest> updateValidator)
    {
        _projectService = projectService;
        _currentUser = currentUser;
        _createValidator = createValidator;
        _updateValidator = updateValidator;
    }

    [HttpGet("workspaces/{id:guid}/projects")]
    [RequireWorkspaceRole(WorkspaceRole.Viewer)]
    public async Task<ActionResult<List<ProjectDto>>> GetProjects(Guid id, CancellationToken cancellationToken)
    {
        var projects = await _projectService.GetProjectsAsync(id, cancellationToken);
        return Ok(projects);
    }

    [HttpPost("workspaces/{id:guid}/projects")]
    [RequireWorkspaceRole(WorkspaceRole.Member)]
    public async Task<ActionResult<ProjectDto>> CreateProject(Guid id, CreateProjectRequest request, CancellationToken cancellationToken)
    {
        await _createValidator.ValidateAndThrowAsync(request, cancellationToken);
        var project = await _projectService.CreateProjectAsync(
            id,
            _currentUser.UserId,
            request,
            Request.Headers.AcceptLanguage.ToString(),
            cancellationToken);
        return Ok(project);
    }

    [HttpPatch("projects/{id:guid}")]
    [RequireWorkspaceRole(WorkspaceRole.Member, WorkspaceResourceType.Project)]
    public async Task<ActionResult<ProjectDto>> UpdateProject(Guid id, UpdateProjectRequest request, CancellationToken cancellationToken)
    {
        await _updateValidator.ValidateAndThrowAsync(request, cancellationToken);
        var project = await _projectService.UpdateProjectAsync(id, request, cancellationToken);
        return Ok(project);
    }

    [HttpDelete("projects/{id:guid}")]
    [RequireWorkspaceRole(WorkspaceRole.Admin, WorkspaceResourceType.Project)]
    public async Task<IActionResult> ArchiveProject(Guid id, CancellationToken cancellationToken)
    {
        await _projectService.ArchiveProjectAsync(id, cancellationToken);
        return NoContent();
    }
}
