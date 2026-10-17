using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.IdentityModel.JsonWebTokens;
using Worklane.Application.Projects.Interfaces;
using Worklane.Application.Workspaces.Interfaces;
using Worklane.WebApi.Services;

namespace Worklane.WebApi.Hubs;

/// <summary>
/// Realtime notifications hub. Clients join a per-project group to receive board
/// broadcasts (task created / moved / edited) and presence (who's online, who's editing
/// which card) — group membership and presence are both checked/tracked here rather than
/// trusted from the client, since a WebSocket connection bypasses the route-based
/// [RequireWorkspaceRole] filter entirely.
/// </summary>
[Authorize]
public class NotificationsHub : Hub
{
    private readonly IProjectService _projects;
    private readonly IWorkspaceService _workspaces;
    private readonly IPresenceTracker _presence;

    public NotificationsHub(IProjectService projects, IWorkspaceService workspaces, IPresenceTracker presence)
    {
        _projects = projects;
        _workspaces = workspaces;
        _presence = presence;
    }

    public async Task JoinProject(Guid projectId)
    {
        var userId = GetUserId();
        if (!await CanJoinAsync(projectId, userId))
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(projectId));
            throw new HubException("You are not a member of this workspace.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(projectId));

        // Membership can be removed between the check and the group add. Drop the group
        // again so a removed member does not stay subscribed.
        if (!await CanJoinAsync(projectId, userId))
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(projectId));
            throw new HubException("You are not a member of this workspace.");
        }

        var justCameOnline = _presence.Join(projectId, userId, Context.ConnectionId);
        await Clients.Caller.SendAsync("PresenceSnapshot", _presence.OnlineUserIds(projectId));
        if (justCameOnline)
            await Clients.OthersInGroup(GroupName(projectId)).SendAsync("UserOnline", userId);
    }

    public async Task LeaveProject(Guid projectId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(projectId));
        await AnnounceLeave(_presence.LeaveProject(Context.ConnectionId, projectId));
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        foreach (var left in _presence.Leave(Context.ConnectionId))
            await AnnounceLeave(left);
        await base.OnDisconnectedAsync(exception);
    }

    public async Task StartEditingTask(Guid taskId)
    {
        var projectId = _presence.ProjectFor(Context.ConnectionId);
        if (projectId is null) return;

        var userId = GetUserId();
        var group = GroupName(projectId.Value);
        var previousTaskId = _presence.SetEditing(Context.ConnectionId, taskId);

        if (previousTaskId is { } prev && prev != taskId)
            await Clients.OthersInGroup(group).SendAsync("TaskEditingStopped", prev, userId);
        await Clients.OthersInGroup(group).SendAsync("TaskEditingStarted", taskId, userId);
    }

    public async Task StopEditingTask(Guid taskId)
    {
        var projectId = _presence.ProjectFor(Context.ConnectionId);
        if (projectId is null) return;

        _presence.SetEditing(Context.ConnectionId, null);
        await Clients.OthersInGroup(GroupName(projectId.Value)).SendAsync("TaskEditingStopped", taskId, GetUserId());
    }

    internal static string GroupName(Guid projectId) => $"project:{projectId}";

    /// <summary>Shared by explicit LeaveProject and an abrupt disconnect: clears a
    /// dangling "editing" badge and tells the group if the user has gone fully offline.</summary>
    private async Task AnnounceLeave(PresenceLeaveResult? result)
    {
        if (result is null) return;

        var group = GroupName(result.ProjectId);
        if (result.EditingTaskId is { } taskId)
            await Clients.Group(group).SendAsync("TaskEditingStopped", taskId, result.UserId);
        if (result.WasLastConnection)
            await Clients.Group(group).SendAsync("UserOffline", result.UserId);
    }

    private async Task<bool> CanJoinAsync(Guid projectId, Guid userId)
    {
        if (userId == Guid.Empty)
            return false;
        if (await _projects.IsArchivedAsync(projectId, Context.ConnectionAborted))
            return false;

        var workspaceId = await _projects.GetWorkspaceIdForProjectAsync(projectId, Context.ConnectionAborted);
        if (workspaceId is null)
            return false;

        var role = await _workspaces.GetUserRoleAsync(workspaceId.Value, userId, Context.ConnectionAborted);
        return role is not null;
    }

    private Guid GetUserId()
    {
        var value = Context.User?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        return value is not null && Guid.TryParse(value, out var id) ? id : Guid.Empty;
    }
}
