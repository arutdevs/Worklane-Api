using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Worklane.Application.Common.Interfaces;
using Worklane.Application.Workspaces.Dtos;
using Worklane.Application.Workspaces.Interfaces;

namespace Worklane.WebApi.Controllers;

[ApiController]
[Route("api/invitations")]
[Authorize]
public class InvitationsController : ControllerBase
{
    private readonly IWorkspaceService _workspaceService;
    private readonly ICurrentUserService _currentUser;
    private readonly IValidator<AcceptInvitationRequest> _acceptValidator;

    public InvitationsController(
        IWorkspaceService workspaceService,
        ICurrentUserService currentUser,
        IValidator<AcceptInvitationRequest> acceptValidator)
    {
        _workspaceService = workspaceService;
        _currentUser = currentUser;
        _acceptValidator = acceptValidator;
    }

    [HttpPost("accept")]
    public async Task<IActionResult> Accept(AcceptInvitationRequest request, CancellationToken cancellationToken)
    {
        await _acceptValidator.ValidateAndThrowAsync(request, cancellationToken);
        await _workspaceService.AcceptInvitationAsync(_currentUser.UserId, request.Token, cancellationToken);
        return NoContent();
    }
}
