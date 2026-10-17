namespace Worklane.Application.Common.Interfaces;

/// <summary>
/// Drops a user's live SignalR connections from project groups when they lose access.
/// </summary>
public interface IRealtimeGroupMembership
{
    Task RemoveUserFromWorkspaceAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken = default);

    Task RemoveUserFromProjectAsync(Guid projectId, Guid userId, CancellationToken cancellationToken = default);
}
