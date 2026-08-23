using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.IdentityModel.JsonWebTokens;
using Worklane.Application.Projects.Interfaces;
using Worklane.Application.Workspaces.Interfaces;

namespace Worklane.WebApi.Hubs;

/// <summary>
/// Realtime notifications hub. Clients join a per-project group to receive board
/// broadcasts (task created / moved / edited) — membership is checked against the same
/// workspace role a REST call would require, since a WebSocket connection bypasses the
/// route-based [RequireWorkspaceRole] filter entirely.
/// </summary>
[Authorize]
public class NotificationsHub : Hub
{
    private readonly IProjectService _projects;
    private readonly IWorkspaceService _workspaces;

    public NotificationsHub(IProjectService projects, IWorkspaceService workspaces)
    {
        _projects = projects;
        _workspaces = workspaces;
    }

    public async Task JoinProject(Guid projectId)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty) return;

        var workspaceId = await _projects.GetWorkspaceIdForProjectAsync(projectId, Context.ConnectionAborted);
        if (workspaceId is null) return;

        var role = await _workspaces.GetUserRoleAsync(workspaceId.Value, userId, Context.ConnectionAborted);
        if (role is null) return;

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(projectId));
    }

    public Task LeaveProject(Guid projectId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(projectId));

    internal static string GroupName(Guid projectId) => $"project:{projectId}";

    private Guid GetUserId()
    {
        var value = Context.User?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        return value is not null && Guid.TryParse(value, out var id) ? id : Guid.Empty;
    }
}
