using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Worklane.Application.Common.Interfaces;
using Worklane.Infrastructure.Persistence;
using Worklane.WebApi.Hubs;

namespace Worklane.WebApi.Services;

/// <summary>
/// Removes a user's open connections from <c>project:{id}</c> when they lose access.
/// A fresh scope is used for the project lookup because this can run after the request
/// scope has finished, and the tenant filter fails closed until a workspace is pinned.
/// </summary>
public class SignalRGroupMembership : IRealtimeGroupMembership
{
    private readonly IHubContext<NotificationsHub> _hub;
    private readonly IPresenceTracker _presence;
    private readonly IServiceScopeFactory _scopes;

    public SignalRGroupMembership(
        IHubContext<NotificationsHub> hub,
        IPresenceTracker presence,
        IServiceScopeFactory scopes)
    {
        _hub = hub;
        _presence = presence;
        _scopes = scopes;
    }

    public async Task RemoveUserFromWorkspaceAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        var projectIds = await db.Projects
            .IgnoreQueryFilters()
            .Where(p => p.WorkspaceId == workspaceId)
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);

        foreach (var projectId in projectIds)
            await RemoveUserFromProjectAsync(projectId, userId, cancellationToken);
    }

    public async Task RemoveUserFromProjectAsync(Guid projectId, Guid userId, CancellationToken cancellationToken = default)
    {
        var group = NotificationsHub.GroupName(projectId);
        foreach (var connectionId in _presence.ConnectionIds(projectId, userId))
        {
            await _hub.Groups.RemoveFromGroupAsync(connectionId, group, cancellationToken);
            var left = _presence.Leave(connectionId);
            if (left is not { WasLastConnection: true }) continue;

            if (left.EditingTaskId is { } taskId)
                await _hub.Clients.Group(group).SendAsync("TaskEditingStopped", taskId, left.UserId, cancellationToken);
            await _hub.Clients.Group(group).SendAsync("UserOffline", left.UserId, cancellationToken);
        }
    }
}
