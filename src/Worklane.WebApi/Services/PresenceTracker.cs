using System.Collections.Concurrent;

namespace Worklane.WebApi.Services;

public sealed record PresenceLeaveResult(Guid ProjectId, Guid UserId, bool WasLastConnection, Guid? EditingTaskId);

/// <summary>
/// In-memory presence for the notifications hub — one process, so this is fine as a
/// singleton keyed by SignalR connection id. Not durable across app restarts, which is
/// acceptable: presence is a live "who's here right now" signal, not a record.
/// </summary>
public interface IPresenceTracker
{
    /// <summary>Registers a connection under a project/user. Returns true if this is that
    /// user's first live connection to the project (i.e. they just came online).</summary>
    bool Join(Guid projectId, Guid userId, string connectionId);

    /// <summary>Removes a connection. Null if the connection was never registered.</summary>
    PresenceLeaveResult? Leave(string connectionId);

    /// <summary>Sets (or clears, with null) which task a connection is editing. Returns the
    /// previously-editing task id, if any, so the caller can announce that one stopped.</summary>
    Guid? SetEditing(string connectionId, Guid? taskId);

    Guid? ProjectFor(string connectionId);

    IReadOnlyCollection<Guid> OnlineUserIds(Guid projectId);

    IReadOnlyCollection<string> ConnectionIds(Guid projectId, Guid userId);
}

public class PresenceTracker : IPresenceTracker
{
    private sealed class ConnectionState
    {
        public required Guid ProjectId { get; init; }
        public required Guid UserId { get; init; }
        public Guid? EditingTaskId { get; set; }
    }

    private readonly ConcurrentDictionary<string, ConnectionState> _connections = new();

    // projectId -> userId -> connectionIds currently open for that user on that project.
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, ConcurrentDictionary<string, byte>>> _projectUsers = new();

    public bool Join(Guid projectId, Guid userId, string connectionId)
    {
        _connections[connectionId] = new ConnectionState { ProjectId = projectId, UserId = userId };

        var users = _projectUsers.GetOrAdd(projectId, _ => new ConcurrentDictionary<Guid, ConcurrentDictionary<string, byte>>());
        var connections = users.GetOrAdd(userId, _ => new ConcurrentDictionary<string, byte>());
        var isFirstConnection = connections.IsEmpty;
        connections[connectionId] = 0;
        return isFirstConnection;
    }

    public PresenceLeaveResult? Leave(string connectionId)
    {
        if (!_connections.TryRemove(connectionId, out var state)) return null;

        var wasLastConnection = true;
        if (_projectUsers.TryGetValue(state.ProjectId, out var users) &&
            users.TryGetValue(state.UserId, out var connections))
        {
            connections.TryRemove(connectionId, out _);
            wasLastConnection = connections.IsEmpty;
            if (wasLastConnection) users.TryRemove(state.UserId, out _);
        }

        return new PresenceLeaveResult(state.ProjectId, state.UserId, wasLastConnection, state.EditingTaskId);
    }

    public Guid? SetEditing(string connectionId, Guid? taskId)
    {
        if (!_connections.TryGetValue(connectionId, out var state)) return null;
        var previous = state.EditingTaskId;
        state.EditingTaskId = taskId;
        return previous;
    }

    public Guid? ProjectFor(string connectionId) =>
        _connections.TryGetValue(connectionId, out var state) ? state.ProjectId : null;

    public IReadOnlyCollection<Guid> OnlineUserIds(Guid projectId) =>
        _projectUsers.TryGetValue(projectId, out var users) ? users.Keys.ToList() : [];

    public IReadOnlyCollection<string> ConnectionIds(Guid projectId, Guid userId)
    {
        if (_projectUsers.TryGetValue(projectId, out var users) &&
            users.TryGetValue(userId, out var connections))
            return connections.Keys.ToList();

        return [];
    }
}
