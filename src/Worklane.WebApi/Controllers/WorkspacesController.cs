using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Worklane.Application.Common.Interfaces;
using Worklane.Application.Workspaces.Dtos;
using Worklane.Application.Workspaces.Interfaces;
using Worklane.Domain.Enums;
using Worklane.WebApi.Filters;

namespace Worklane.WebApi.Controllers;

[ApiController]
[Route("api/workspaces")]
[Authorize]
public class WorkspacesController : ControllerBase
{
    private readonly IWorkspaceService _workspaceService;
    private readonly ICurrentUserService _currentUser;
    private readonly IValidator<CreateWorkspaceRequest> _createValidator;
    private readonly IValidator<InviteMemberRequest> _inviteValidator;
    private readonly IValidator<UpdateMemberRoleRequest> _updateRoleValidator;

    public WorkspacesController(
        IWorkspaceService workspaceService,
        ICurrentUserService currentUser,
        IValidator<CreateWorkspaceRequest> createValidator,
        IValidator<InviteMemberRequest> inviteValidator,
        IValidator<UpdateMemberRoleRequest> updateRoleValidator)
    {
        _workspaceService = workspaceService;
        _currentUser = currentUser;
        _createValidator = createValidator;
        _inviteValidator = inviteValidator;
        _updateRoleValidator = updateRoleValidator;
    }

    [HttpGet]
    public async Task<ActionResult<List<WorkspaceDto>>> GetMine(CancellationToken cancellationToken)
    {
        var workspaces = await _workspaceService.GetMyWorkspacesAsync(_currentUser.UserId, cancellationToken);
        return Ok(workspaces);
    }

    [HttpPost]
    public async Task<ActionResult<WorkspaceDto>> Create(CreateWorkspaceRequest request, CancellationToken cancellationToken)
    {
        await _createValidator.ValidateAndThrowAsync(request, cancellationToken);
        var workspace = await _workspaceService.CreateWorkspaceAsync(_currentUser.UserId, request, cancellationToken);
        return Ok(workspace);
    }

    [HttpGet("{id:guid}/members")]
    [RequireWorkspaceRole(WorkspaceRole.Viewer)]
    public async Task<ActionResult<List<WorkspaceMemberDto>>> GetMembers(Guid id, CancellationToken cancellationToken)
    {
        var members = await _workspaceService.GetMembersAsync(id, cancellationToken);
        return Ok(members);
    }

    [HttpPost("{id:guid}/invitations")]
    [RequireWorkspaceRole(WorkspaceRole.Admin)]
    public async Task<ActionResult<InvitationDto>> Invite(Guid id, InviteMemberRequest request, CancellationToken cancellationToken)
    {
        await _inviteValidator.ValidateAndThrowAsync(request, cancellationToken);
        var invitation = await _workspaceService.InviteMemberAsync(id, _currentUser.UserId, request, cancellationToken);
        return Ok(invitation);
    }

    [HttpPatch("{id:guid}/members/{userId:guid}")]
    [RequireWorkspaceRole(WorkspaceRole.Admin)]
    public async Task<IActionResult> UpdateMemberRole(Guid id, Guid userId, UpdateMemberRoleRequest request, CancellationToken cancellationToken)
    {
        await _updateRoleValidator.ValidateAndThrowAsync(request, cancellationToken);
        await _workspaceService.UpdateMemberRoleAsync(id, _currentUser.UserId, userId, request.Role, cancellationToken);
        return NoContent();
    }

    [HttpDelete("{id:guid}/members/{userId:guid}")]
    [RequireWorkspaceRole(WorkspaceRole.Admin)]
    public async Task<IActionResult> RemoveMember(Guid id, Guid userId, CancellationToken cancellationToken)
    {
        await _workspaceService.RemoveMemberAsync(id, _currentUser.UserId, userId, cancellationToken);
        return NoContent();
    }
}
